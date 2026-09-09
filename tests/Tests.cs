using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading.Tasks;

namespace LabVpnConnect
{
    internal static class Tests
    {
        private static int passed;
        private static void Assert(bool ok, string message) { if (!ok) throw new Exception(message); }
        private static void Reject(Action action)
        {
            bool rejected = false;
            try { action(); } catch (ArgumentException) { rejected = true; } catch (InvalidOperationException) { rejected = true; }
            Assert(rejected, "Expected rejection.");
        }
        private static Adapter Adapter(string ip, bool up, NetworkInterfaceType type)
        {
            return new Adapter { Id = "vpn-id", Name = "Lab VPN", Index = 7, Up = up, Type = type, Addresses = new[] { IPAddress.Parse(ip) } };
        }
        private static void Test(string name, Action run) { run(); passed++; Console.WriteLine("PASS " + name); }
        private static int Main(string[] args)
        {
            if (args.Length == 1 && args[0] == "--stdio-echo")
            {
                using (Stream input = StandardStreams.Open(-10, FileAccess.Read))
                using (Stream output = StandardStreams.Open(-11, FileAccess.Write)) input.CopyTo(output);
                return 0;
            }
            try
            {
                Test("strict arguments and literal IPv4", () => {
                    Options o = Options.Parse(new[] { "--interface", "Lab VPN", "--host", "203.0.113.10", "--port", "10137" });
                    Assert(o.Port == 10137 && o.InterfaceName == "Lab VPN", "Argument parsing");
                    foreach (string bad in new[] { "example.com", "127.1", "0x7f.0.0.1", "1.2.3.999", "1.2.3.-1", "::1" })
                        Reject(() => Options.ParseIPv4(bad));
                    Reject(() => Options.Parse(new[] { "--interface", "lab", "--host", "203.0.113.10", "--port", "0" }));
                    Reject(() => Options.Parse(new[] { "--interface", "lab", "--interface", "lab" }));
                });
                Test("VPN missing or down never selects Ethernet", () => {
                    Reject(() => Vpn.Select(new Adapter[0], "Lab VPN"));
                    Reject(() => Vpn.Select(new[] { Adapter("10.20.0.12", false, NetworkInterfaceType.Ppp) }, "Lab VPN"));
                    Reject(() => Vpn.Select(new[] { Adapter("192.168.1.2", true, NetworkInterfaceType.Ethernet) }, "Lab VPN"));
                });
                Test("VPN IPv4 is discovered anew", () => {
                    Assert(Vpn.Select(new[] { Adapter("10.20.0.12", true, NetworkInterfaceType.Ppp) }, "lab vpn").Addresses[0].ToString() == "10.20.0.12", "First address");
                    Assert(Vpn.Select(new[] { Adapter("10.20.0.19", true, NetworkInterfaceType.Ppp) }, "lab vpn").Addresses[0].ToString() == "10.20.0.19", "Changed address");
                });
                Test("ambiguous or unaddressed VPN is rejected", () => {
                    Adapter a = Adapter("10.20.0.12", true, NetworkInterfaceType.Ppp);
                    Reject(() => Vpn.Select(new[] { a, a }, "Lab VPN"));
                    a.Addresses = new IPAddress[0];
                    Reject(() => Vpn.Select(new[] { a }, "Lab VPN"));
                });
                Test("binary relay preserves 1 MiB and TCP half-close", () => {
                    var listener = new TcpListener(IPAddress.Loopback, 0);
                    listener.Start();
                    var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                    client.Connect(listener.LocalEndpoint);
                    Socket server = listener.AcceptSocket();
                    listener.Stop();
                    byte[] payload = new byte[1024 * 1024];
                    new Random(7).NextBytes(payload);
                    byte[] reply = new byte[] { 0, 255, 13, 10, 26, 128, 0, 42 };
                    var serverTask = Task.Run(() => {
                        using (server)
                        using (var received = new MemoryStream())
                        {
                            byte[] buffer = new byte[1009];
                            int n;
                            while ((n = server.Receive(buffer)) > 0) received.Write(buffer, 0, n);
                            Assert(received.ToArray().SequenceEqual(payload), "Upload was corrupted.");
                            for (int i = 0; i < reply.Length; i++) server.Send(reply, i, 1, SocketFlags.None);
                            server.Shutdown(SocketShutdown.Send);
                        }
                    });
                    var output = new MemoryStream();
                    var relayTask = Task.Run(() => Transport.Relay(client, new MemoryStream(payload), output));
                    Assert(Task.WaitAll(new[] { serverTask, relayTask }, 10000), "Relay hung.");
                    Assert(output.ToArray().SequenceEqual(reply), "Download was corrupted.");
                });
                Console.WriteLine(passed + " tests passed.");
                return 0;
            }
            catch (Exception e) { Console.Error.WriteLine(e); return 1; }
        }
    }
}
