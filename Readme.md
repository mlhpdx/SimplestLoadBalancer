![Build](https://github.com/mlhpdx/SimplestLoadBalancer/workflows/Build/badge.svg)
[![Maintainability Rating](https://sonarcloud.io/api/project_badges/measure?project=mlhpdx_SimplestLoadBalancer&metric=sqale_rating)](https://sonarcloud.io/dashboard?id=mlhpdx_SimplestLoadBalancer)
[![Reliability Rating](https://sonarcloud.io/api/project_badges/measure?project=mlhpdx_SimplestLoadBalancer&metric=reliability_rating)](https://sonarcloud.io/dashboard?id=mlhpdx_SimplestLoadBalancer)
[![Security Rating](https://sonarcloud.io/api/project_badges/measure?project=mlhpdx_SimplestLoadBalancer&metric=security_rating)](https://sonarcloud.io/dashboard?id=mlhpdx_SimplestLoadBalancer)
[![Lines of Code](https://sonarcloud.io/api/project_badges/measure?project=mlhpdx_SimplestLoadBalancer&metric=ncloc)](https://sonarcloud.io/dashboard?id=mlhpdx_SimplestLoadBalancer)

# Simplest UDP Load Balancer #

This code demonstrates sessionless load balancing of UDP traffic, solving problems inherent with using traditional load balancers for such traffic. 

![bar](udp-slb.jpg)

## Why? ##

Some simple UDP protocols are stateless and there is no advantage in trying to maintain "affinity" between clients and back-end instances.  Traditional load balancers assume that affinity is helpful, and so they will try to route packets from one client to one server. By contrast, this code demonstrates a load balancer that evenly (randomly) distributes packets over all available back-ends. One advantage of this approach for "simple" UDP is that if one backend instance fails there will be an increase in packet loss for all clients rather than a loss of all traffic for some clients (as traditional load balancers would do).

## Building ##

This is a very simple .Net Core 3.1 project, so to build (assuming you have the SDK installed):

```
dotnet build
```

If you'd like to generate a single-file executable, which is convenient just target the platform you'll be running on. For Linux:

```
dotnet publish -o ./ -c Release -r linux-x64 /p:PublishSingleFile=true /p:PublishTrimmed=true --self-contained
```

Or for Windows:

```
dotnet publish -o ./ -c Release -r win10-x64 /p:PublishSingleFile=true /p:PublishTrimmed=true --self-contained
```

## Usage ##
Likewise, it's simple to run using `dotnet run` in the project directory:

```
$ dotnet run
```

Or, if you've build a native executable:

```
$ ./SimplestLoadBalancer
```


By default the process will listen on ports `1812` and 1813 for any incomming UDP packets and will relay them one to one of the IP addres/port targets it knows.  You can control the port it listens on with the `--server-port-range` option.  Other options are described in the command help:

```
SimplestLoadBalancer:
  Sessionless UDP Load Balancer sends packets to targets without session affinity.

Usage:
  SimplestLoadBalancer [options]

Options:
  --server-port-range <server-port-range>            Set the port to listen to and forward to backend targets (default "1812-1813")
  --admin-port <admin-port>                          Set the port that targets will send watchdog events (default 1111)
  --client-timeout <client-timeout>                  Seconds to allow before cleaning-up idle clients (default 30)
  --target-timeout <target-timeout>                  Seconds to allow before removing target missing watchdog events (default 30)
  --default-target-weight <default-target-weight>    Weight to apply to targets when not specified (default 100)
  --unwise                                           Allows public IP addresses for targets (default is to only allow private IPs)
  --version                                          Show version information
  -?, -h, --help                                     Show help and usage information
```

In context of the image at the top of this document, the `--server-point-range` corresponds to "E1" (the client-facing external port).

To add and maintain targets send periodic UDP packets to the admin port (default 1111) on the first private IPv4 address configured on your machine.  The packet format is very simple, consisting of a couple magic bytes to avoid spurrious adds, four bytes for an ipv4 address and two byes for the port number to target:

```
0x11 0x11 [four bytes for ip] [two bytes for port] [one byte for weight (optional)]
```

The weight is optional, and if not in the packet the value specified at the command line will be used.  Weights are applied in the traditional manner.  

Each time such a packet is received the backend's "last seen" time is updated. If 30 seconds passes without a backend being seen, it is removed. To immeadiately remove a target send a packet with 0x86 as the first byte instead of 0x11:

```
0x86 0x11 [four bytes for ip] [two bytes for port]
```

Using Linux `bash` it's easy to send those packets using the filesystem object `/dev/udp/[admin ip]/[admin port]`. For example, if your load balancer is listening on the default admin port `1111` at `192.168.1.11`, and you want to add a target with the IP `192.168.1.22` and port number `1812`:

```bash
$ echo -e $(echo  "\x11\x11$(echo "192.168.1.22" | tr "." "\n" | xargs printf '\\x%02X')\x14\x07") > /dev/udp/192.168.1.11/1111
```
To use a different port on the target, just change the `\x14\07` to the little endian hex representation of port number of your choice.

It can be tedius to manually send those packets and keep a target registered. A simple way to maintain a set of IPs as targets for testing is to create a small shell script, say `lb.sh`:

```bash
#!/bin/bash
echo -ne $(echo  "\x11\x11$(echo "192.168.1.22" | tr "." "\n" | xargs printf '\\x%02X')\x14\x07") > /dev/udp/192.168.1.11/1111
echo -ne $(echo  "\x11\x11$(echo "192.168.1.23" | tr "." "\n" | xargs printf '\\x%02X')\x14\x07") > /dev/udp/192.168.1.11/1111
echo -ne $(echo  "\x11\x11$(echo "192.168.1.24" | tr "." "\n" | xargs printf '\\x%02X')\x14\x07") > /dev/udp/192.168.1.11/1111
echo -ne $(echo  "\x11\x11$(echo "192.168.1.25" | tr "." "\n" | xargs printf '\\x%02X')\x14\x07") > /dev/udp/192.168.1.11/1111
echo -ne $(echo  "\x11\x11$(echo "192.168.1.26" | tr "." "\n" | xargs printf '\\x%02X')\x14\x07") > /dev/udp/192.168.1.11/1111
echo -ne $(echo  "\x11\x11$(echo "192.168.1.27" | tr "." "\n" | xargs printf '\\x%02X')\x14\x07") > /dev/udp/192.168.1.11/1111
```

And then use the `watch` command to call that script every few seconds:

```bash
$ watch -n10 ./lb.sh
```

Enjoy!
