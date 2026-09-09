using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;

namespace LabVpnConnect
{
    internal sealed class ConnectedVpn
    {
        internal string Name;
        internal IPAddress ServerAddress;
    }

    internal static class RasDiscovery
    {
        // ras.h uses pshpack4.h even on 64-bit Windows.
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode, Pack = 4)]
        private struct RasConnection
        {
            public int Size;
            public IntPtr Handle;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 257)] public string EntryName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 17)] public string DeviceType;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 129)] public string DeviceName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string Phonebook;
            public uint SubEntry;
            public Guid EntryId;
            public uint Flags;
            public long LogonId;
            public Guid CorrelationId;
        }

        [StructLayout(LayoutKind.Sequential, Pack = 4)]
        private struct Endpoint
        {
            public uint Type;
            public uint Part1, Part2, Part3, Part4;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode, Pack = 4)]
        private struct ConnectionStatus
        {
            public int Size;
            public uint State, Error;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 17)] public string DeviceType;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 129)] public string DeviceName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 129)] public string PhoneNumber;
            public Endpoint Local, Remote;
            public uint SubState;
        }

        [DllImport("rasapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern uint RasEnumConnectionsW(IntPtr buffer, ref int bytes, out int count);

        [DllImport("rasapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern uint RasGetConnectStatusW(IntPtr handle, ref ConnectionStatus status);

        internal static ConnectedVpn[] Enumerate()
        {
            const uint BufferTooSmall = 603;
            int bytes = 0, count;
            uint code = RasEnumConnectionsW(IntPtr.Zero, ref bytes, out count);
            if (code == 0 && count == 0) return new ConnectedVpn[0];
            if (code != BufferTooSmall) throw new InvalidOperationException("Cannot enumerate Windows VPN connections (RAS error " + code + ").");
            int stride = Marshal.SizeOf(typeof(RasConnection));
            for (int attempt = 0; attempt < 4; attempt++)
            {
                if (bytes < stride || bytes > 16 * 1024 * 1024) throw new InvalidOperationException("Invalid RAS connection buffer size.");
                int allocated = bytes;
                IntPtr buffer = Marshal.AllocHGlobal(allocated);
                try
                {
                    Marshal.WriteInt32(buffer, stride);
                    code = RasEnumConnectionsW(buffer, ref bytes, out count);
                    if (code == BufferTooSmall) continue; // Connections changed during enumeration.
                    if (code != 0) throw new InvalidOperationException("Cannot enumerate Windows VPN connections (RAS error " + code + ").");
                    if (count < 0 || count > allocated / stride) throw new InvalidOperationException("Invalid RAS connection count.");
                    var result = new List<ConnectedVpn>();
                    for (int i = 0; i < count; i++)
                    {
                        var connection = (RasConnection)Marshal.PtrToStructure(IntPtr.Add(buffer, i * stride), typeof(RasConnection));
                        var status = new ConnectionStatus { Size = Marshal.SizeOf(typeof(ConnectionStatus)) };
                        if (RasGetConnectStatusW(connection.Handle, ref status) != 0 || status.State != 0x2000 || status.Error != 0)
                            continue;
                        if (!string.Equals(connection.DeviceType, "vpn", StringComparison.OrdinalIgnoreCase) || status.Remote.Type != 1)
                            continue;
                        // Match the actual outer IPv4 endpoint. This also works when the
                        // profile uses a DNS name, without issuing our own DNS lookup.
                        result.Add(new ConnectedVpn { Name = connection.EntryName,
                            ServerAddress = new IPAddress(BitConverter.GetBytes(status.Remote.Part1)) });
                    }
                    return result.ToArray();
                }
                finally { Marshal.FreeHGlobal(buffer); }
            }
            throw new InvalidOperationException("VPN connections are changing; try again after the VPN is connected.");
        }

        internal static string Match(IEnumerable<ConnectedVpn> connections, IPAddress target)
        {
            ConnectedVpn[] matches = connections.Where(c => c.ServerAddress.Equals(target)).ToArray();
            if (matches.Length == 0)
                throw new InvalidOperationException("No connected Windows VPN has server IP " + target + ". Connect the matching VPN first, or use --interface NAME explicitly. No Internet fallback is used.");
            if (matches.Length > 1)
                throw new InvalidOperationException("Multiple connected VPNs have server IP " + target + ": " +
                    string.Join(", ", matches.Select(c => c.Name)) + ". Select one with --interface NAME.");
            return matches[0].Name;
        }
    }
}
