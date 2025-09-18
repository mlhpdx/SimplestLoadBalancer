using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using DotMake.CommandLine;

Cli.Run(async (string serverPortRange = "1812-1813", string internalPortRange = "32048-62048", IPAddress adminIp = default, int adminPort = 1111, uint clientTimeout = 30, uint targetTimeout = 30, byte defaultTargetWeight = 100, bool unwise = false, ushort statsPeriodMs = 1000, byte defaultGroupId = 0, bool useProxyProtocol = false, string[] proxyProtocolTLV = default) =>
  await SimplestLoadBalancer.Program.RunAsync(serverPortRange, internalPortRange, adminIp, adminPort, clientTimeout, targetTimeout, defaultTargetWeight, unwise, statsPeriodMs, defaultGroupId, useProxyProtocol, proxyProtocolTLV));

namespace SimplestLoadBalancer
{
  static class Extensions
  {
    public static IEnumerable<int> Enumerate(this (int from, int to) range) => Enumerable.Range(range.from, range.to - range.from + 1);

    static readonly Random rand = new();
    public static K Random<K, V>(this IDictionary<K, (byte weight, V)> items)
    {
      var n = rand.Next(0, items.Values.Sum(v => v.weight));
      return items.FirstOrDefault(kv => (n -= kv.Value.weight) < 0).Key;
    }

    public static void SendVia(this IPEndPoint backend, UdpClient client, byte[] packet, AsyncCallback cb) =>
    client.BeginSend(packet, packet.Length, backend, cb, null);

    public static IEnumerable<IPAddress> Private(this NetworkInterface[] interfaces) =>
    interfaces.Where(i => i.OperationalStatus == OperationalStatus.Up)
      .Where(i => i.NetworkInterfaceType != NetworkInterfaceType.Loopback)
      .SelectMany(i => i.GetIPProperties().UnicastAddresses)
      .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork || a.Address.AddressFamily == AddressFamily.InterNetworkV6)
      .Where(a => IPNetwork2.IsIANAReserved(a.Address))
      .Select(a => a.Address);

    public const int SIO_UDP_CONNRESET = -1744830452;
    public static UdpClient Configure(this UdpClient client)
    {
      client.DontFragment = true;
      // client.Client.IOControl((IOControlCode)SIO_UDP_CONNRESET, new byte[] { 0, 0, 0, 0 }, null); // don't throw on disconnect
      return client;
    }

    private static UdpClient MakeSinglecastClient(this IPEndPoint ep) => new UdpClient(ep).Configure();

    private static UdpClient MakeMulticastClient(this IPEndPoint ep)
    {
      var udp = new UdpClient(new IPEndPoint(IPAddress.Any, ep.Port));
      udp.JoinMulticastGroup(ep.Address);
      return udp.Configure();
    }

    private static bool IsMulticast(this IPAddress ip) => ip.IsIPv6Multicast ||
      (ip.AddressFamily == AddressFamily.InterNetwork && (ip.GetAddressBytes()[0] & 0b11100000) == 0b11100000);

    public static UdpClient MakeUdpClient(this IPEndPoint ep) => ep.Address.IsMulticast() ? ep.MakeMulticastClient() : ep.MakeSinglecastClient();
  }

  static class Program
  {
    static long received = 0L;
    static long relayed = 0L;
    static long responded = 0L;

    /// <summary>
    /// Sessionless UDP Load Balancer sends packets to targets without session affinity.
    /// </summary>
    /// <param name="serverPortRange">Set the ports to listen to and forward to backend targets (can't overlap with internalPortRange or contain adminPort)</param>
    /// <param name="internalPortRange">Set the ports to use to forward to backend targets (can't overlap with serverPortRange or contain adminPort)</param>
    /// <param name="adminIp">Set the IP to listen on for watchdog events (default is first private IP)</param>
    /// <param name="adminPort">Set the port that targets will send watchdog events</param>
    /// <param name="clientTimeout">Seconds to allow before cleaning-up idle clients</param>
    /// <param name="targetTimeout">Seconds to allow before removing target missing watchdog events</param>
    /// <param name="defaultTargetWeight">Weight to apply to targets when not specified</param>
    /// <param name="unwise">Allows public IP addresses for targets</param>
    /// <param name="statsPeriodMs">Sets the number of milliseconds between statistics messages printed to the console (disable: 0, max: 65535)</param>
    /// <param name="defaultGroupId">Sets the group ID to assign to backends that when a registration packet doesn't include one, and when port isn't assigned a group</param>
    /// <param name="useProxyProtocol">When specified packet data will be prepended with a Proxy Protocol v2 header when sent to the backend</param>
    /// <param name="proxyProtocolTLV">Use to specify one or more TLVs to add to PPv2 headers (ignored when PPv2 isn't enabled). Example value: "0xDA=smurf".</param>
    public static async Task RunAsync(string serverPortRange, string internalPortRange, IPAddress adminIp, int adminPort, uint clientTimeout, uint targetTimeout, byte defaultTargetWeight, bool unwise, ushort statsPeriodMs, byte defaultGroupId, bool useProxyProtocol, string[] proxyProtocolTLV)
    {
      var ports = serverPortRange.Split("-", StringSplitOptions.RemoveEmptyEntries) switch
      {
        string[] a when a.Length == 1 => [int.Parse(a[0])],
        string[] a when a.Length == 2 => (from: int.Parse(a[0]), to: int.Parse(a[1])).Enumerate().ToArray(),
        _ => throw new ArgumentException($"Invalid server port range: {serverPortRange}.", nameof(serverPortRange))
      };
      var internal_ports = internalPortRange.Split("-", StringSplitOptions.RemoveEmptyEntries) switch
      {
        string[] a when a.Length == 1 => [int.Parse(a[0])],
        string[] a when a.Length == 2 => (from: int.Parse(a[0]), to: int.Parse(a[1])).Enumerate().ToArray(),
        _ => throw new ArgumentException($"Invalid internal port range: {internalPortRange}.", nameof(internalPortRange))
      };

      if (ports.Intersect(internal_ports).Any())
      {
        throw new ArgumentException($"Server and internal port ranges must not overlap and mustn't include the admin port: {serverPortRange}, {internalPortRange} and {adminPort}.", nameof(serverPortRange));
      }

      await Console.Out.WriteLineAsync($"{DateTime.UtcNow:s}: Welcome to the simplest UDP Load Balancer.  Hit Ctrl-C to Stop.");

      var my_ip = NetworkInterface.GetAllNetworkInterfaces().Private().First();
      var admin_ip = adminIp ?? my_ip;
      await Console.Out.WriteLineAsync($"{DateTime.UtcNow:s}: The server port range is {serverPortRange} ({ports.Length} port{(ports.Length > 1 ? "s" : "")}).");
      await Console.Out.WriteLineAsync($"{DateTime.UtcNow:s}: The internal port range is {internalPortRange} ({internal_ports.Length} port{(internal_ports.Length > 1 ? "s" : "")}).");
      await Console.Out.WriteLineAsync($"{DateTime.UtcNow:s}: The admin/watchdog endpoint is {admin_ip}:{adminPort}.");
      await Console.Out.WriteLineAsync($"{DateTime.UtcNow:s}: Timeouts are: {clientTimeout}s for clients, and {targetTimeout}s for targets.");
      await Console.Out.WriteLineAsync($"{DateTime.UtcNow:s}: Proxy Protocol v2 for targets is {(useProxyProtocol ? "enabled" : "disabled")}.");
      await Console.Out.WriteLineAsync($"{DateTime.UtcNow:s}: {(unwise ? "*WARNING* " : string.Empty)}"
        + $"Targets with public IPs {(unwise ? "WILL BE" : "will NOT be")} allowed.");

      using var cts = new CancellationTokenSource();

      Console.CancelKeyPress += (s, a) =>
      {
        Console.Out.WriteLine($"{DateTime.UtcNow:s}: Beginning shutdown procedure.");
        cts.Cancel();
        a.Cancel = true;
      };

      // helper to run tasks with cancellation
      Task run(Func<Task> func, string name)
      {
        return Task.Run(async () =>
        {
          var ct = cts.Token;
          while (!ct.IsCancellationRequested)
          {
            try
            {
              await func();
            }
            catch (Exception e)
            {
              await Console.Out.WriteLineAsync($"{DateTime.UtcNow:s}: *ERROR* Task {name} encountered a problem: {e.Message}");
              await Task.Delay(100); // slow fail
            }
          }
          await Console.Out.WriteLineAsync($"{DateTime.UtcNow:s}: {name} is done.");
        });
      }

      var backend_groups = new ConcurrentDictionary<byte, ConcurrentDictionary<IPAddress, (byte weight, DateTime seen)>>();
      var port_group_map = new ConcurrentDictionary<int, byte>(ports.ToDictionary(p => p, p => defaultGroupId));

      var clients = new ConcurrentDictionary<(IPEndPoint remote, int external_port), (int internal_port, UdpClient internal_client, DateTime seen)>();
      var servers = ports.ToDictionary(p => p, p => new UdpClient(p).Configure());
      var free_internal_ports = new Queue<int>(internal_ports);

      // helper to get requests (inbound packets from external sources) asyncronously
      async IAsyncEnumerable<(UdpReceiveResult result, int port)> requests()
      {
        foreach (var s in servers)
          if (s.Value.Available > 0)
            yield return (await s.Value.ReceiveAsync(), s.Key);
      }

      byte[] arg_to_tlv(string arg)
      {
        (var type, var val) = arg.Split('=', 2, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries) switch
        {
          [['0', 'x', .. var t], string v] when t?.Length <= 2 => v switch
          {
            ['0', 'x', .. var hex] => (Convert.FromHexString(t), Convert.FromHexString(hex)),
            _ => (Convert.FromHexString(t), System.Text.Encoding.UTF8.GetBytes(v))
          },
          _ => (null, null)
        };
        var len = 3 + val.Length;
        return type == null ? [] : [.. type, (byte)(len / 256), (byte)(len % 256), .. val];
      }
      var tlv_bytes = (proxyProtocolTLV ?? []).SelectMany(arg_to_tlv).ToArray();
      Memory<byte> ppv2_header(IPEndPoint src, int dst_port)
      {
        return src.Address.AddressFamily switch
        {
          AddressFamily.InterNetwork or AddressFamily.InterNetworkV6 => (byte[])[
            0x0D, 0x0A, 0x0D, 0x0A, 0x00, 0x0D, 0x0A, 0x51, 0x55, 0x49, 0x54, 0x0A, // signature
            0x21, // version 2, proxied
#pragma warning disable CS8509 // The outer switch expression ensures the innner expression may see only the two possible states
            .. src.Address.AddressFamily switch {
              AddressFamily.InterNetwork => (byte[])[
                0x12, // IP(v4) UDP
                (byte)((12 + tlv_bytes.Length) / 256), (byte)((12 + tlv_bytes.Length) % 256), // 12 bytes of address
                .. src.Address.GetAddressBytes(),
                .. (my_ip.AddressFamily == AddressFamily.InterNetwork ? my_ip : IPAddress.None).GetAddressBytes()
              ],
              AddressFamily.InterNetworkV6 => [
                0x22, // IP(v6) UDP
                (byte)((36 + tlv_bytes.Length) / 256), (byte)((36 + tlv_bytes.Length) % 256), // 36 bytes of address
                .. src.Address.GetAddressBytes(),
                .. (my_ip.AddressFamily == AddressFamily.InterNetworkV6 ? my_ip : IPAddress.IPv6None).GetAddressBytes()
              ]
            },
#pragma warning restore CS8509 // 
            (byte)(src.Port / 256), (byte)(src.Port % 256),
            (byte)(dst_port / 256), (byte)(dst_port % 256),
            ..tlv_bytes
          ],
          _ => Memory<byte>.Empty
        };
      }

      // task to listen on the server port and relay packets to random backends via a client-specific internal port
      async Task relay()
      {
        var any = false;
        await foreach (var (request, port) in requests())
        {
          Interlocked.Increment(ref received);

          var (_, internal_client, _) = clients.AddOrUpdate((request.RemoteEndPoint, port),
            ep =>
            {
              var internal_port = free_internal_ports.Dequeue();
              return (internal_port, new UdpClient().Configure(), DateTime.UtcNow);
            },
            (ep, c) => (c.internal_port, c.internal_client, DateTime.UtcNow)
          );
          if (backend_groups.TryGetValue(port_group_map[port], out var group))
          {
            var backend = group.Random();
            var header = useProxyProtocol ? ppv2_header(request.RemoteEndPoint, port) : Memory<byte>.Empty;
            new IPEndPoint(backend, port).SendVia(internal_client, [.. header.Span, .. request.Buffer], s => Interlocked.Increment(ref relayed));
          }
          any = true;
        }
        if (!any) await Task.Delay(10); // slack the loop
      }

      // helper to get replies asyncronously
      async IAsyncEnumerable<(UdpReceiveResult result, IPEndPoint ep, int port)> replies()
      {
        var any = false;
        foreach (var c in clients)
        {
          if (c.Value.internal_client.Available > 0)
          {
            yield return (await c.Value.internal_client.ReceiveAsync(), c.Key.remote, c.Key.external_port);
            any = true;
          }
        }
        if (!any) await Task.Delay(10); // slack the loop
      }

      // task to listen for responses from backends and re-send them to the correct external client
      async Task reply()
      {
        var any = false;
        await foreach (var (result, ep, port) in replies())
        {
          servers[port].BeginSend(result.Buffer, result.Buffer.Length, ep, s => Interlocked.Increment(ref responded), null);
          any = true;
        }
        if (!any) await Task.Delay(10); // slack the loop
      }

      // task to listen for instances asking to add/remove themselves as a target (watch-dog pattern)
      using var control = new IPEndPoint(admin_ip, adminPort).MakeUdpClient();
      var ep_none = new IPEndPoint(IPAddress.None, 0);
      async Task admin()
      {
        if (control.Available > 0)
        {
          var packet = await control.ReceiveAsync();
          var payload = new ArraySegment<byte>(packet.Buffer);

          (IPAddress ip, byte weight, byte group_id) get_ip_weight_and_group(ArraySegment<byte> command) =>
            command switch
            {
              // eight bytes for ip, then options
              [_, _, _, _, _, _, _, _, .. var options] =>
                (
                  ip: command switch
                  {
                    [0, 0, 0, 0, 0, 0, 0, 0, ..] => packet.RemoteEndPoint.Address,
                    _ => new IPAddress(command.Slice(0, 8))
                  },
                  weight: options switch { [var weight, ..] => weight, _ => defaultTargetWeight },
                  group_id: options switch { [_, var group, ..] => group, _ => defaultGroupId }
                ),
              // four bytes for ip, then options
              [_, _, _, _, .. var options] =>
                (
                  ip: command switch
                  {
                    [0, 0, 0, 0, ..] => packet.RemoteEndPoint.Address,
                    _ => new IPAddress(command.Slice(0, 4))
                  },
                  weight: options switch { [var weight, ..] => weight, _ => defaultTargetWeight },
                  group_id: options switch { [_, var group, ..] => group, _ => defaultGroupId }
                ),
              // less than four bytes, just options
              [.. var options] =>
                (
                  ip: packet.RemoteEndPoint.Address,
                  weight: options switch { [var weight, ..] => weight, _ => defaultTargetWeight },
                  group_id: options switch { [_, var group, ..] => group, _ => defaultGroupId }
                )
            };

          switch (payload)
          {
            case [0x66, 0x11, var port_low, var port_high, var group]:
              {
                var port = port_low + (port_high << 8);
                port_group_map.AddOrUpdate(port, p => group, (p, g) => group);
                await Console.Out.WriteLineAsync($"{DateTime.UtcNow:s}: Mapped port {port} to group {group}.");
              }
              break;

            case [0x11, 0x11, .. var command]:
              {
                (var ip, var weight, var group_id) = get_ip_weight_and_group(command);
                if (unwise || IPNetwork2.IsIANAReserved(ip))
                {
                  var group = backend_groups.AddOrUpdate(group_id, id => new(), (id, g) => g);
                  if (group != null)
                  {
                    if (weight > 0)
                    {
                      group.AddOrUpdate(ip, i => (weight, DateTime.UtcNow), (i, d) => (weight, DateTime.UtcNow));
                      await Console.Out.WriteLineAsync($"{DateTime.UtcNow:s}: Refresh {ip} (weight {weight}, group {group_id}).");
                    }
                    else await Console.Out.WriteLineAsync($"{DateTime.UtcNow}: Rejected zero-weighted {ip} for group {group_id}.");
                  }
                  else await Console.Out.WriteLineAsync($"${DateTime.UtcNow:s}: Rejected invalid backend group {group_id} for ip {ip}.");
                }
                else await Console.Out.WriteLineAsync($"{DateTime.UtcNow:s}: Rejected {ip}.");
              }
              break;

            case [0x86, 0x11, .. var command]:
              {// see AIEE No. 26
                (var ip, var _, var group_id) = get_ip_weight_and_group(command);
                if (backend_groups.TryGetValue(group_id, out var group))
                  group.Remove(ip, out var _);
                await Console.Out.WriteLineAsync($"{DateTime.UtcNow:s}: Remove {ip} from group {group_id}.");
              }
              break;

            case [0x2e, 0x11, var port_high, var port_low, .. var ep_bytes]:
              {
                var (client_ep, server_port) = (ep_bytes switch
                {
                  [var client_port_high, var client_port_low, .. var ip_bytes] when ip_bytes.Count == 4 || ip_bytes.Count == 16
                    => new IPEndPoint(new IPAddress(ep_bytes[2..]), client_port_low + (client_port_high << 8)),
                  _ => ep_none
                }, port_low + (port_high << 8));

                if (clients.TryGetValue((client_ep, server_port), out var info))
                {
                  var internal_port = info.internal_port;
                  await control.SendAsync([0x2e, 0x12, (byte)(internal_port >> 8), (byte)internal_port, port_high, port_low, .. ep_bytes], 6 + ep_bytes.Count, packet.RemoteEndPoint);
                }
              }
              break;

            default:
              await Console.Out.WriteLineAsync($"{DateTime.UtcNow:s}: Ignored bad/unrecognized control packet from {packet.RemoteEndPoint}.");
              break;
          }
        }
        else await Task.Delay(10);
      }

      // task to remove backends and clients we haven't heard from in a while
      async Task prune()
      {
        await Task.Delay(100);
        foreach (var backends in backend_groups.Values)
        {
          var remove_backends = backends.Where(kv => kv.Value.seen < DateTime.UtcNow.AddSeconds(-targetTimeout)).Select(kv => kv.Key).ToArray();
          foreach (var b in remove_backends)
          {
            backends.TryRemove(b, out var seen);
            await Console.Out.WriteLineAsync($"{DateTime.UtcNow:s}: Expired target {b} (last seen {seen:s}).");
          }
        }
        var remove_clients = clients.Where(kv => kv.Value.seen < DateTime.UtcNow.AddSeconds(-clientTimeout)).Select(kv => kv.Key).ToArray();
        foreach (var c in remove_clients)
        {
          clients.TryRemove(c, out var info);
          info.internal_client.Dispose();
          free_internal_ports.Enqueue(info.internal_port);
          await Console.Out.WriteLineAsync($"{DateTime.UtcNow:s}: Expired client {c} (last seen {info.seen:s}).");
        }
      }

      // task to occassionally write statistics to the console
      async Task stats()
      {
        await Console.Out.WriteLineAsync($"{DateTime.UtcNow:s}: {received}/{relayed}/{responded}, {clients.Count} => {backend_groups.Count}/{backend_groups.Sum(g => g.Value.Count)}({backend_groups.Values.SelectMany(g => g).Distinct().Count()})");
        try { await Task.Delay(statsPeriodMs, cts.Token); } catch { /* suppress cancel */ }
      }

      var tasks = new[] {
          run(relay, "Relay"),
          run(reply, "Reply"),
          run(admin, "Admin"),
          run(prune, "Prune")
        }.ToList();

      if (statsPeriodMs > 0)
        tasks.Add(run(stats, "State"));

      await Task.WhenAll(tasks);
      var e = string.Join(", ", tasks.Where(t => t.Exception != null).Select(t => t.Exception.Message));
      await Console.Out.WriteLineAsync($"{DateTime.UtcNow:s}: Bye-now ({(e.Length != 0 ? e : "OK")}).");
    }
  }
}
