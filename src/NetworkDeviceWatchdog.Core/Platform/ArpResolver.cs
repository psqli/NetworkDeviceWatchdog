using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace NetworkDeviceWatchdog.Platform;

/// <summary>Finds the MAC address that answers for an IPv4 address on the local subnet.</summary>
public interface IArpResolver
{
    /// <summary>The MAC address, or null when it cannot be determined.</summary>
    PhysicalAddress? Resolve(IPAddress address);
}

/// <summary>
/// Windows: asks the network stack with <c>SendARP</c> (sends an ARP request if needed).
/// Linux: reads the kernel ARP cache (<c>/proc/net/arp</c>), which the preceding ping fills.
/// Other systems: not supported (returns null).
/// </summary>
public sealed partial class SystemArpResolver : IArpResolver
{
    public PhysicalAddress? Resolve(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork)
            return null;
        if (OperatingSystem.IsWindows())
            return ResolveWindows(address);
        if (OperatingSystem.IsLinux())
            return ParseProcNetArp(SafeRead("/proc/net/arp"), address);
        return null;
    }

    private static PhysicalAddress? ResolveWindows(IPAddress address)
    {
        // SendARP wants the address bytes in network order as they lie in memory.
        var dest = BitConverter.ToUInt32(address.GetAddressBytes(), 0);
        var mac = new byte[6];
        uint length = (uint)mac.Length;
        try
        {
            var rc = SendARP(dest, 0, mac, ref length);
            return rc == 0 && length == 6 ? new PhysicalAddress(mac) : null;
        }
        catch (DllNotFoundException)
        {
            return null;
        }
        catch (EntryPointNotFoundException)
        {
            return null;
        }
    }

    [LibraryImport("iphlpapi.dll")]
    private static partial int SendARP(uint destIp, uint srcIp, [Out] byte[] macAddr, ref uint physicalAddrLen);

    private static string? SafeRead(string path)
    {
        try { return File.ReadAllText(path); }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    /// <summary>
    /// Parses the Linux ARP table:
    /// <c>IP address  HW type  Flags  HW address  Mask  Device</c>.
    /// Flags 0x0 means the entry is incomplete.
    /// </summary>
    internal static PhysicalAddress? ParseProcNetArp(string? table, IPAddress address)
    {
        if (table is null)
            return null;
        var wanted = address.ToString();
        foreach (var line in table.Split('\n').Skip(1))
        {
            var f = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (f.Length < 4 || f[0] != wanted)
                continue;
            if (!int.TryParse(f[2].Replace("0x", "", StringComparison.OrdinalIgnoreCase), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var flags) || flags == 0)
                return null;
            var hex = f[3].Replace(":", "", StringComparison.Ordinal);
            return hex.Length == 12 && hex != "000000000000" ? PhysicalAddress.Parse(hex.ToUpperInvariant()) : null;
        }
        return null;
    }
}
