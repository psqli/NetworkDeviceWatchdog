using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using NetworkDeviceWatchdog.Localization;
using NetworkDeviceWatchdog.Platform;

namespace NetworkDeviceWatchdog.Checks;

/// <summary>ICMP echo. Fails with <see cref="FailureCode.Unreachable"/>.</summary>
public sealed class PingCheck(string host, TimeSpan timeout) : IDeviceCheck
{
    public string Name => "ping";

    public async Task<CheckResult> RunAsync(CancellationToken cancellationToken)
    {
        using var ping = new Ping();
        try
        {
            var reply = await ping.SendPingAsync(host, timeout, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (reply.Status == IPStatus.Success)
                return CheckResult.Ok();
        }
        catch (PingException)
        {
            // Name resolution failures and similar end up here: treat as unreachable.
        }
        return CheckResult.Fail(FailureCode.Unreachable, Msg.Of("msg.unreachable", ("host", host)));
    }
}

/// <summary>
/// Compares the MAC address that answers for the device's IP with the expected one.
/// Fails with <see cref="FailureCode.IpConflict"/>. Silently passes when the MAC
/// cannot be determined (device on another subnet, unsupported OS).
/// </summary>
public sealed class MacCheck(string host, PhysicalAddress expected, IArpResolver arp) : IDeviceCheck
{
    public string Name => "mac";

    public async Task<CheckResult> RunAsync(CancellationToken cancellationToken)
    {
        var ip = (await Net.ResolveAsync(host, cancellationToken).ConfigureAwait(false))
            .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);
        if (ip is null)
            return CheckResult.Ok();
        var seen = arp.Resolve(ip);
        if (seen is null || seen.Equals(PhysicalAddress.None) || seen.Equals(expected))
            return CheckResult.Ok();
        return CheckResult.Fail(FailureCode.IpConflict, Msg.Of("msg.ipConflict",
            ("ip", ip.ToString()), ("seen", FormatMac(seen)), ("expected", FormatMac(expected))));
    }

    public static string FormatMac(PhysicalAddress mac) =>
        string.Join('-', mac.GetAddressBytes().Select(b => b.ToString("X2", System.Globalization.CultureInfo.InvariantCulture)));
}

/// <summary>Each TCP port must accept a connection. Fails with <see cref="FailureCode.PortBlocked"/>.</summary>
public sealed class TcpPortCheck(string host, IReadOnlyList<int> ports, TimeSpan timeout, bool rawPrintHint = false) : IDeviceCheck
{
    public string Name => "tcp:" + string.Join(',', ports);

    public async Task<CheckResult> RunAsync(CancellationToken cancellationToken)
    {
        foreach (var port in ports)
        {
            var (socket, error) = await Net.ConnectAsync(host, port, timeout, cancellationToken).ConfigureAwait(false);
            if (socket is null)
                return CheckResult.Fail(FailureCode.PortBlocked, PortBlocked(host, port, error!, rawPrintHint));
            socket.Dispose();
        }
        return CheckResult.Ok();
    }

    internal static Msg PortBlocked(string host, int port, Msg detail, bool rawPrintHint)
    {
        var main = Msg.Of("msg.portBlocked", ("host", host), ("port", port), ("detail", detail));
        if (!rawPrintHint)
            return main;
        // Two sentences: the explanation plus a hint for raw (port 9100) printers.
        return Msg.Of("joined", ("a", main), ("b", Msg.Of("msg.portBlocked.rawHint", ("port", port))));
    }
}

/// <summary>Active TCP connections of this computer.</summary>
public interface IConnectionTable
{
    /// <summary>True if this computer has an established connection to the endpoint.</summary>
    bool HasEstablishedConnection(IReadOnlyCollection<IPAddress> addresses, int port);
}

/// <summary>Reads the operating system's TCP table (Windows, Linux, macOS).</summary>
public sealed class SystemConnectionTable : IConnectionTable
{
    public bool HasEstablishedConnection(IReadOnlyCollection<IPAddress> addresses, int port)
    {
        try
        {
            return IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpConnections().Any(c =>
                c.State == TcpState.Established &&
                c.RemoteEndPoint.Port == port &&
                addresses.Any(a => a.Equals(c.RemoteEndPoint.Address) ||
                                   (c.RemoteEndPoint.Address.IsIPv4MappedToIPv6 && a.Equals(c.RemoteEndPoint.Address.MapToIPv4()))));
        }
        catch (NetworkInformationException)
        {
            return false;
        }
        catch (PlatformNotSupportedException)
        {
            return false;
        }
    }
}

/// <summary>
/// Reports <see cref="CheckOutcome.Busy"/> while this computer (normally its print
/// spooler) has the device's raw print port open, so the probe does not compete
/// for printers that accept only one connection at a time.
/// </summary>
public sealed class LocalConnectionBusyCheck(string host, int port, IConnectionTable table) : IDeviceCheck
{
    public string Name => "busy:" + port;

    public async Task<CheckResult> RunAsync(CancellationToken cancellationToken)
    {
        var addresses = await Net.ResolveAsync(host, cancellationToken).ConfigureAwait(false);
        return addresses.Length > 0 && table.HasEstablishedConnection(addresses, port)
            ? CheckResult.Busy()
            : CheckResult.Ok();
    }
}

/// <summary>A Windows print queue job that needs attention.</summary>
public sealed record PrintQueueProblem(int JobId, string Document, string Status);

/// <summary>Inspects print queues (implemented on Windows).</summary>
public interface IPrintQueueInspector
{
    /// <summary>Returns the first job in an error state, or null.</summary>
    PrintQueueProblem? FindProblem(string queueName);
}

/// <summary>Fails with <see cref="FailureCode.QueueError"/> when the Windows queue has a job in error.</summary>
public sealed class PrintQueueCheck(string queueName, IPrintQueueInspector inspector) : IDeviceCheck
{
    public string Name => "queue";

    public Task<CheckResult> RunAsync(CancellationToken cancellationToken)
    {
        var problem = inspector.FindProblem(queueName);
        return Task.FromResult(problem is null
            ? CheckResult.Ok()
            : CheckResult.Fail(FailureCode.QueueError, Msg.Of("msg.queueError",
                ("queue", queueName), ("job", problem.JobId), ("document", problem.Document), ("status", problem.Status))));
    }
}
