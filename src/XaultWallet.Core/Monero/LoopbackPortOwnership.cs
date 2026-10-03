using System.Globalization;
using System.Runtime.InteropServices;

namespace XaultWallet.Core.Monero;

/// <summary>
/// Answers "is the process listening on 127.0.0.1:<c>port</c> really <c>pid</c>?" before the app
/// sends a seed to that port.
///
/// Why: the wallet-rpc child is told its port on the command line, which other local users can
/// read (/proc/&lt;pid&gt;/cmdline). A process that binds the port first makes the child fail to
/// bind, then answers our requests itself — and digest auth cannot stop it, because digest only
/// authenticates the CLIENT (monero sends no rspauth). Checking the socket's owner closes that race.
/// </summary>
internal static class LoopbackPortOwnership
{
    /// <summary>True/false when the platform can tell; null when it cannot (e.g. macOS).</summary>
    public static bool? IsListenerOwnedBy(int pid, int port)
    {
        try
        {
            if (OperatingSystem.IsLinux())
            {
                return LinuxOwns(pid, port);
            }

            if (OperatingSystem.IsWindows())
            {
                return WindowsOwns(pid, port);
            }
        }
        catch
        {
            // Fall through: an unreadable table is "can't tell", never "yes".
        }

        return null;
    }

    // ---------------- Linux: /proc/net/tcp + /proc/<pid>/fd ----------------

    private static bool? LinuxOwns(int pid, int port)
    {
        // local_address is "0100007F:1F90" (IPv4 in host byte order as hex, then the port); st 0A = LISTEN.
        // A wildcard listener (00000000:port) would also receive our loopback connections, so EVERY
        // listener that could answer must be the child's — not merely one of them (SO_REUSEPORT).
        string hexPort = ":" + port.ToString("X4", CultureInfo.InvariantCulture);
        var listeners = new HashSet<string>(StringComparer.Ordinal);
        foreach (string line in File.ReadLines("/proc/net/tcp").Skip(1))
        {
            string[] f = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (f.Length > 9 && f[3] == "0A" && (f[1] == "0100007F" + hexPort || f[1] == "00000000" + hexPort))
            {
                listeners.Add(f[9]);
            }
        }

        if (listeners.Count == 0)
        {
            return false; // nobody (visible) is listening there
        }

        var childSockets = new HashSet<string>(StringComparer.Ordinal);
        foreach (string fd in Directory.EnumerateFileSystemEntries($"/proc/{pid}/fd"))
        {
            string? target = new FileInfo(fd).LinkTarget; // "socket:[12345]"
            if (target is not null && target.StartsWith("socket:[", StringComparison.Ordinal) && target.EndsWith(']'))
            {
                childSockets.Add(target["socket:[".Length..^1]);
            }
        }

        return listeners.All(childSockets.Contains);
    }

    // ---------------- Windows: GetExtendedTcpTable(TCP_TABLE_OWNER_PID_LISTENER) ----------------

    private const int AfInet = 2;
    private const int TcpTableOwnerPidListener = 3;
    private const uint NoError = 0;
    private const uint ErrorInsufficientBuffer = 122;

    [StructLayout(LayoutKind.Sequential)]
    private struct MibTcpRowOwnerPid
    {
        public uint State;
        public uint LocalAddr;
        public uint LocalPort;
        public uint RemoteAddr;
        public uint RemotePort;
        public uint OwningPid;
    }

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(
        IntPtr pTcpTable, ref int pdwSize, [MarshalAs(UnmanagedType.Bool)] bool bOrder, int ulAf, int tableClass, uint reserved);

    private static bool? WindowsOwns(int pid, int port)
    {
        int size = 0;
        uint rc = GetExtendedTcpTable(IntPtr.Zero, ref size, false, AfInet, TcpTableOwnerPidListener, 0);
        for (int attempt = 0; attempt < 4 && rc == ErrorInsufficientBuffer; attempt++)
        {
            IntPtr buf = Marshal.AllocHGlobal(size);
            try
            {
                rc = GetExtendedTcpTable(buf, ref size, false, AfInet, TcpTableOwnerPidListener, 0);
                if (rc != NoError)
                {
                    continue; // table grew between calls; retry with the new size
                }

                int count = Marshal.ReadInt32(buf);
                int rowSize = Marshal.SizeOf<MibTcpRowOwnerPid>();
                bool sawPort = false;
                for (int i = 0; i < count; i++)
                {
                    var row = Marshal.PtrToStructure<MibTcpRowOwnerPid>(buf + 4 + (i * rowSize));
                    // dwLocalPort holds the port in network byte order in its low 16 bits.
                    int rowPort = (int)(((row.LocalPort & 0xFF) << 8) | ((row.LocalPort >> 8) & 0xFF));
                    if (rowPort != port)
                    {
                        continue;
                    }

                    sawPort = true;
                    if (row.OwningPid != (uint)pid)
                    {
                        return false; // someone else listens there (alone or alongside the child)
                    }
                }

                return sawPort;
            }
            finally
            {
                Marshal.FreeHGlobal(buf);
            }
        }

        return null;
    }
}
