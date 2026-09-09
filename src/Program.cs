using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace LabVpnConnect
{
    internal static class StandardStreams
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GetStdHandle(int kind);

        internal static Stream Open(int kind, FileAccess access)
        {
            var handle = new SafeFileHandle(GetStdHandle(kind), false);
            // Win32 OpenSSH supplies overlapped pipe handles. Console.OpenStandardInput
            // assumes synchronous handles on .NET Framework and can stall after the banner.
            try { return new FileStream(handle, access, 32768, true); }
            catch (ArgumentException) { return new FileStream(handle, access, 32768, false); }
        }
    }
    internal sealed class Options
    {
        internal string InterfaceName;
        internal IPAddress Host;
        internal int Port;
        internal int TimeoutSeconds = 10;
        internal bool Check;
        internal bool Verbose;

        internal static Options Parse(string[] args)
        {
            var o = new Options();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < args.Length; i++)
            {
                string key = args[i];
                if (!seen.Add(key)) throw new ArgumentException("Duplicate option: " + key);
                if (key == "--check") { o.Check = true; continue; }
                if (key == "--verbose") { o.Verbose = true; continue; }
                if (key != "--interface" && key != "--host" && key != "--port" && key != "--timeout")
                    throw new ArgumentException("Unknown option: " + key);
                if (++i >= args.Length) throw new ArgumentException("Missing value for " + key);
                string value = args[i];
                if (key == "--interface") o.InterfaceName = value;
                else if (key == "--host") o.Host = ParseIPv4(value);
                else
                {
                    int number;
                    if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out number))
                        throw new ArgumentException("Invalid number for " + key);
                    if (key == "--port") o.Port = number;
                    else o.TimeoutSeconds = number;
                }
            }
            if (seen.Contains("--interface") && string.IsNullOrWhiteSpace(o.InterfaceName))
                throw new ArgumentException("--interface must not be empty.");
            if (o.Host == null) throw new ArgumentException("--host is required (IPv4 address).");
            if (o.Port < 1 || o.Port > 65535) throw new ArgumentException("--port must be 1-65535.");
            if (o.TimeoutSeconds < 1 || o.TimeoutSeconds > 120) throw new ArgumentException("--timeout must be 1-120 seconds.");
            return o;
        }

        internal static IPAddress ParseIPv4(string value)
        {
            string[] parts = value.Split('.');
            byte part;
            if (parts.Length != 4 || parts.Any(p => p.Length == 0 || p.Any(c => c < '0' || c > '9') ||
                !byte.TryParse(p, NumberStyles.None, CultureInfo.InvariantCulture, out part)))
                throw new ArgumentException("Use a numeric IPv4 address, not a hostname.");
            return new IPAddress(parts.Select(p => byte.Parse(p, CultureInfo.InvariantCulture)).ToArray());
        }
    }

    internal sealed class Adapter
    {
        internal string Id;
        internal string Name;
        internal bool Up;
        internal NetworkInterfaceType Type;
        internal int Index;
        internal IPAddress[] Addresses;
    }

    internal static class Vpn
    {
        internal static Adapter[] Enumerate()
        {
            var result = new List<Adapter>();
            foreach (NetworkInterface nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                try
                {
                    IPInterfaceProperties p = nic.GetIPProperties();
                    IPv4InterfaceProperties v4 = p.GetIPv4Properties();
                    result.Add(new Adapter {
                        Id = nic.Id, Name = nic.Name,
                        Up = nic.OperationalStatus == OperationalStatus.Up,
                        Type = nic.NetworkInterfaceType, Index = v4 == null ? 0 : v4.Index,
                        Addresses = p.UnicastAddresses.Select(a => a.Address)
                            .Where(a => a.AddressFamily == AddressFamily.InterNetwork &&
                                !IPAddress.IsLoopback(a) && !a.Equals(IPAddress.Any) &&
                                !(a.GetAddressBytes()[0] == 169 && a.GetAddressBytes()[1] == 254)).ToArray()
                    });
                }
                catch (NetworkInformationException) { /* Adapter can disappear during enumeration. */ }
            }
            return result.ToArray();
        }

        internal static Adapter Select(IEnumerable<Adapter> adapters, string name)
        {
            Adapter[] matches = adapters.Where(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (matches.Length != 1 || !matches[0].Up)
                throw new InvalidOperationException("VPN '" + name + "' is not connected. Connect it first; no Internet fallback is used.");
            Adapter found = matches[0];
            if (found.Type != NetworkInterfaceType.Ppp && found.Type != NetworkInterfaceType.Tunnel)
                throw new InvalidOperationException("The selected interface is not a PPP/Tunnel VPN adapter.");
            if (found.Index <= 0 || found.Addresses.Length != 1)
                throw new InvalidOperationException("The VPN must have exactly one usable IPv4 address.");
            return found;
        }

        internal static bool StillConnected(Adapter selected)
        {
            try
            {
                Adapter current = Select(Enumerate(), selected.Name);
                return current.Id == selected.Id && current.Index == selected.Index &&
                    current.Addresses[0].Equals(selected.Addresses[0]);
            }
            catch { return false; }
        }
    }

    internal static class Transport
    {
        internal static Socket Connect(Adapter vpn, Options o)
        {
            var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            try
            {
                // IP_UNICAST_IF = 31. Windows requires the interface index in network byte order.
                // This affects only this socket, leaving the outer VPN transport's route intact.
                socket.SetSocketOption(SocketOptionLevel.IP, (SocketOptionName)31, IPAddress.HostToNetworkOrder(vpn.Index));
                socket.Bind(new IPEndPoint(vpn.Addresses[0], 0));
                socket.NoDelay = true;
                IAsyncResult pending = socket.BeginConnect(new IPEndPoint(o.Host, o.Port), null, null);
                using (WaitHandle ready = pending.AsyncWaitHandle)
                {
                    if (!ready.WaitOne(TimeSpan.FromSeconds(o.TimeoutSeconds)))
                        throw new TimeoutException("Connection timed out over the VPN.");
                    socket.EndConnect(pending);
                }
                return socket;
            }
            catch { socket.Dispose(); throw; }
        }

        internal static void Relay(Socket socket, Stream input, Stream output, Action<string> trace = null)
        {
            Exception uploadError = null;
            var upload = new Thread(() => {
                try
                {
                    var bytes = new byte[32768];
                    int count;
                    while ((count = input.Read(bytes, 0, bytes.Length)) > 0)
                    {
                        if (trace != null) trace("stdin bytes: " + count);
                        int sent = 0;
                        while (sent < count)
                        {
                            int n = socket.Send(bytes, sent, count - sent, SocketFlags.None);
                            if (n == 0) throw new IOException("The connection closed while sending.");
                            sent += n;
                        }
                    }
                    // EOF from SSH closes only the sending direction; still deliver pending replies.
                    socket.Shutdown(SocketShutdown.Send);
                }
                catch (Exception e) { Interlocked.CompareExchange(ref uploadError, e, null); socket.Close(); }
            });
            upload.IsBackground = true;
            upload.Start();
            try
            {
                var bytes = new byte[32768];
                int count;
                while ((count = socket.Receive(bytes)) > 0)
                {
                    if (trace != null) trace("network bytes: " + count);
                    output.Write(bytes, 0, count);
                    output.Flush();
                }
                Exception error = Interlocked.CompareExchange(ref uploadError, null, null);
                if (error != null) throw new IOException("Could not relay SSH input.", error);
            }
            finally { socket.Close(); }
        }
    }

    internal static class Program
    {
        internal const string Version = "1.1.0";
        private static int Main(string[] args)
        {
            if (args.Length == 1 && args[0] == "--version") { Console.WriteLine("lab-vpn-connect " + Version); return 0; }
            if (args.Length == 0 || (args.Length == 1 && args[0] == "--help"))
            {
                Console.WriteLine("lab-vpn-connect --host IPv4 --port PORT [--interface NAME] [--timeout SECONDS] [--check] [--verbose]");
                Console.WriteLine("By default, finds the connected Windows VPN whose server IPv4 equals --host.");
                Console.WriteLine("For SSH ProxyCommand on Windows PPP/Tunnel VPN adapters. Diagnostics go to stderr.");
                return args.Length == 0 ? 2 : 0;
            }
            Options options;
            Adapter selected;
            try { options = Options.Parse(args); }
            catch (Exception e) { Console.Error.WriteLine("lab-vpn-connect: " + e.Message); return 2; }
            try
            {
                string name = options.InterfaceName ?? RasDiscovery.Match(RasDiscovery.Enumerate(), options.Host);
                selected = Vpn.Select(Vpn.Enumerate(), name);
            }
            catch (Exception e) { Console.Error.WriteLine("lab-vpn-connect: " + e.Message); return 3; }
            try
            {
                using (Socket socket = Transport.Connect(selected, options))
                {
                    int vpnLost = 0;
                    NetworkAddressChangedEventHandler changed = (sender, e) => {
                        if (!Vpn.StillConnected(selected))
                        {
                            Interlocked.Exchange(ref vpnLost, 1);
                            socket.Close();
                        }
                    };
                    NetworkChange.NetworkAddressChanged += changed;
                    try
                    {
                        if (!Vpn.StillConnected(selected)) throw new IOException("VPN changed during connection setup.");
                        if (options.Check)
                            Console.Error.WriteLine("Connected via VPN '{0}' (interface {1}, source {2}) to {3}:{4}.",
                                selected.Name, selected.Index, selected.Addresses[0], options.Host, options.Port);
                        else
                            Transport.Relay(socket, StandardStreams.Open(-10, FileAccess.Read), StandardStreams.Open(-11, FileAccess.Write),
                                options.Verbose ? (Action<string>)(message => Console.Error.WriteLine(message)) : null);
                        if (Interlocked.CompareExchange(ref vpnLost, 0, 0) != 0)
                            throw new IOException("VPN disconnected; connection stopped.");
                    }
                    finally { NetworkChange.NetworkAddressChanged -= changed; }
                }
                return 0;
            }
            catch (Exception e) { Console.Error.WriteLine("lab-vpn-connect: " + e.Message); return 4; }
        }
    }
}
