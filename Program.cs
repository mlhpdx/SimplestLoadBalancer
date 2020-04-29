using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace SimplestLoadBalancer
{
    static class Extensions
    {
        static readonly Random rand = new Random();
        public static K Random<K, V>(this IDictionary<K, (byte weight, V)> items)
        {
            var n = rand.Next(0, items.Values.Sum(v => v.weight));
            return items.FirstOrDefault(kv => (n -= kv.Value.weight) < 0).Key;
        }

        public static void SendVia(this IPEndPoint ep, UdpClient client, byte[] packet, AsyncCallback cb) =>
            client.BeginSend(packet, packet.Length, ep, cb, null);

        public static IEnumerable<IPAddress> Private(this NetworkInterface[] interfaces) =>
            interfaces.Where(i => i.OperationalStatus == OperationalStatus.Up)
                .Where(i => i.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .SelectMany(i => i.GetIPProperties().UnicastAddresses)
                .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork)
                .Where(a => IPNetwork.IsIANAReserved(a.Address))
                .Select(a => a.Address);

        public const int SIO_UDP_CONNRESET = -1744830452;
        public static UdpClient Configure(this UdpClient client)
        {
            client.DontFragment = true;
//            client.Client.IOControl((IOControlCode)SIO_UDP_CONNRESET, new byte[] { 0, 0, 0, 0 }, null); // don't throw on disconnect
            return client;
        }
    }
    static class Program
    {
        static long received = 0L;
        static long relayed = 0L;
        static long responded = 0L;

        /// <summary>
        /// Sessionless UDP Load Balancer sends packets to targets without session affinity.
        /// </summary>
        /// <param name="serverPort">Set the port to listen to and forward to backend targets (default 1812)</param>
        /// <param name="adminPort">Set the port that targets will send watchdog events (default 1111)</param>
        /// <param name="clientTimeout">Seconds to allow before cleaning-up idle clients (default 30)</param>
        /// <param name="targetTimeout">Seconds to allow before removing target missing watchdog events (default 30)</param>
        /// <param name="defaultTargetWeight">Weight to apply to targets when not specified (default 100)</param>
        /// <param name="unwise">Allows public IP addresses for targets (default is to only allow private IPs)</param>
        static async Task Main(int serverPort = 1812, int adminPort = 1111, uint clientTimeout = 30, uint targetTimeout = 30, byte defaultTargetWeight = 100, bool unwise = false)
        {
            await Console.Out.WriteLineAsync($"{DateTime.Now:s}: Welcome to the simplest UDP Load Balancer.  Hit Ctrl-C to Stop.");

            var admin_ip = NetworkInterface.GetAllNetworkInterfaces().Private().First();
            await Console.Out.WriteLineAsync($"{DateTime.Now:s}: The server port is {serverPort}.");
            await Console.Out.WriteLineAsync($"{DateTime.Now:s}: The watchdog endpoint is {admin_ip}:{adminPort}.");
            await Console.Out.WriteLineAsync($"{DateTime.Now:s}: Timeouts are: {clientTimeout}s for clients, and {targetTimeout}s  for targets.");
            await Console.Out.WriteLineAsync($"{DateTime.Now:s}: {(unwise ? "*WARNING* " : string.Empty)}"
                + $"Targets with public IPs {(unwise ? "WILL BE" : "will NOT be")} allowed.");

            using var cts = new CancellationTokenSource();

            Console.CancelKeyPress += (s, a) =>
            {
                Console.Out.WriteLine($"{DateTime.Now:s}: Beginning shutdown procedure.");
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
                            await Console.Out.WriteLineAsync($"{DateTime.Now:s}: *ERROR* Task {name} encountered a problem: {e.Message}");
                            await Task.Delay(100); // slow fail
                        }
                    }
                    await Console.Out.WriteLineAsync($"{DateTime.Now:s}: {name} is done.");
                });
            }

            var backends = new ConcurrentDictionary<IPEndPoint, (byte weight, DateTime seen)>();
            var clients = new ConcurrentDictionary<IPEndPoint, (UdpClient client, DateTime seen)>();

            // task to listen on the server port and relay packets to random backends via a client-specific internal port
            using var server = new UdpClient(serverPort).Configure();
            async Task relay()
            {
                if (server.Available > 0)
                {
                    var packet = await server.ReceiveAsync();

                    Interlocked.Increment(ref received);

                    var client = clients.AddOrUpdate(packet.RemoteEndPoint, ep => (new UdpClient().Configure(), DateTime.Now), (ep, c) => (c.client, DateTime.Now));
                    var backend = backends.Random();
                    backend?.SendVia(client.client, packet.Buffer, s => Interlocked.Increment(ref relayed));
                }
                else await Task.Delay(10);
            }

            // helper to get replies asyncronously
            async IAsyncEnumerable<(UdpReceiveResult result, IPEndPoint ep)> replies()
            {
                foreach (var c in clients)
                    if (c.Value.client.Available > 0)
                        yield return (await c.Value.client.ReceiveAsync(), c.Key);
            }

            // task to listen for responses from backends and re-send them to the correct external client
            async Task reply()
            {
                var any = false;
                await foreach (var (result, ep) in replies())
                {
                    server.BeginSend(result.Buffer, result.Buffer.Length, ep, s => Interlocked.Increment(ref responded), null);
                    any = true;
                }
                if (!any) await Task.Delay(10);
            }

            // task to listen for instances asking to add/remove themselves as a target (watch-dog pattern)
            using var control = new UdpClient(new IPEndPoint(admin_ip, adminPort)).Configure();
            async Task admin()
            {
                if (control.Available > 0)
                {
                    var packet = await control.ReceiveAsync();
                    var payload = new ArraySegment<byte>(packet.Buffer);

                    var header = payload.Slice(0, 2);
                    var ip = new IPAddress(payload.Slice(2).Slice(0, 4));
                    if (ip.Equals(IPAddress.Any)) ip = packet.RemoteEndPoint.Address;
                    var port = BitConverter.ToUInt16(payload.Slice(6).Slice(0, 2));
                    var weight = payload.Count > 8 ? payload[8] : defaultTargetWeight;
                    if (weight > 0 && (unwise || IPNetwork.IsIANAReserved(ip)))
                    {
                        var ep = new IPEndPoint(ip, port);
                        switch (BitConverter.ToInt16(header))
                        {
                            case 0x1111:
                                backends.AddOrUpdate(ep, ep => (weight, DateTime.Now), (ep, d) => (weight, DateTime.Now));
                                await Console.Out.WriteLineAsync($"{DateTime.Now:s}: Refresh {ep} (weight {weight}).");
                                break;
                            case 0x1186: // see AIEE No. 26
                                backends.Remove(ep, out var seen);
                                await Console.Out.WriteLineAsync($"{DateTime.Now:s}: Remove {ep}.");
                                break;
                        }
                    }
                    else await Console.Out.WriteLineAsync($"{DateTime.Now:s}: Rejected {ip}:{port} (weight {weight}).");
                }
                else await Task.Delay(10);
            }

            // task to remove backends and clients we haven't heard from in a while
            async Task purge()
            {
                await Task.Delay(100);
                var remove_backends = backends.Where(kv => kv.Value.seen < DateTime.Now.AddSeconds(-targetTimeout)).Select(kv => kv.Key).ToArray();
                foreach (var b in remove_backends)
                {
                    backends.TryRemove(b, out var seen);
                    await Console.Out.WriteLineAsync($"{DateTime.Now:s}: Expired target {b} (last seen {seen:s}).");
                }
                var remove_clients = clients.Where(kv => kv.Value.seen < DateTime.Now.AddSeconds(-clientTimeout)).Select(kv => kv.Key).ToArray();
                foreach (var c in remove_clients)
                {
                    clients.TryRemove(c, out var info);
                    await Console.Out.WriteLineAsync($"{DateTime.Now:s}: Expired client {c} (last seen {info.seen:s}).");
                }
            }

            // task to occassionally write statistics to the console
            async Task stats()
            {
                await Console.Out.WriteLineAsync($"{DateTime.Now:s}: {received}/{relayed}/{responded}, {clients.Count} => {backends.Count}");
                await Task.Delay(500);
            }

            var tasks = new[] {
                run(relay, "Relay"),
                run(reply, "Reply"),
                run(admin, "Admin"),
                run(purge, "Purge"),
                run(stats, "State")
            };
            await Task.WhenAll(tasks);
            var e = string.Join(", ", tasks.Where(t => t.Exception != null).Select(t => t.Exception.Message));
            await Console.Out.WriteLineAsync($"{DateTime.Now:s}: Bye-now ({(e.Any() ? e : "OK")})");
        }
    }
}
