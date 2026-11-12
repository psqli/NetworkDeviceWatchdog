using NetworkDeviceWatchdog.Configuration;
using NetworkDeviceWatchdog.Localization;
using NetworkDeviceWatchdog.Platform;

namespace NetworkDeviceWatchdog.Checks;

/// <summary>
/// Runs a device's checks in order. The first check that does not pass decides
/// the result, so the cheapest and most basic checks come first (busy, ping, MAC)
/// and the diagnosis is the most specific one that applies.
/// </summary>
public sealed class DeviceProbe(IReadOnlyList<IDeviceCheck> checks) : IDisposable
{
    public IReadOnlyList<IDeviceCheck> Checks { get; } = checks;

    public async Task<CheckResult> RunAsync(CancellationToken cancellationToken)
    {
        var notes = new List<Msg>();
        foreach (var check in Checks)
        {
            CheckResult result;
            try
            {
                result = await check.RunAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception e)
            {
                result = CheckResult.Fail(FailureCode.CheckFailed, Msg.Of("msg.checkFailed", ("detail", $"{check.Name}: {e.Message}")));
            }

            if (result.Outcome != CheckOutcome.Ok)
                return result.WithNotes(notes);
            notes.AddRange(result.Notes);
        }
        return CheckResult.Ok(notes);
    }

    public void Dispose()
    {
        foreach (var check in Checks.OfType<IDisposable>())
            check.Dispose();
    }
}

/// <summary>Operating-system services the checks depend on (replaceable in tests).</summary>
public sealed class ProbeServices
{
    public IArpResolver Arp { get; init; } = new SystemArpResolver();

    public IConnectionTable Connections { get; init; } = new SystemConnectionTable();

    /// <summary>Windows print queue access; null where unavailable.</summary>
    public IPrintQueueInspector? PrintQueues { get; init; }
}

/// <summary>Builds the check pipeline for a configured device.</summary>
public static class DeviceProbeFactory
{
    public static DeviceProbe Create(DeviceConfig device, TimeoutConfig timeouts, ProbeServices services)
    {
        if (!DeviceKinds.TryParse(device.Type, out var kind))
            throw new ArgumentException($"Unknown device type \"{device.Type}\".", nameof(device));

        var host = device.Host.Trim();
        var connect = TimeSpan.FromMilliseconds(timeouts.ConnectMs);
        var read = TimeSpan.FromMilliseconds(timeouts.ReadMs);
        var port = device.Port ?? kind.DefaultPort();
        var checks = new List<IDeviceCheck>();

        if (kind == DeviceKind.EscPosPrinter)
            checks.Add(new LocalConnectionBusyCheck(host, port, services.Connections));

        if (device.Ping)
            checks.Add(new PingCheck(host, TimeSpan.FromMilliseconds(timeouts.PingMs)));

        if (!string.IsNullOrWhiteSpace(device.Mac) && ConfigLoader.TryParseMac(device.Mac, out var mac))
            checks.Add(new MacCheck(host, mac, services.Arp));

        switch (kind)
        {
            case DeviceKind.EscPosPrinter:
                checks.Add(new EscPosStatusCheck(host, port, connect, read));
                break;
            case DeviceKind.Printer when device.Ipp?.Enabled != false:
                var ipp = device.Ipp ?? new IppConfig();
                checks.Add(new IppCheck(host, port, ipp.Path, ipp.Tls, TimeSpan.FromMilliseconds(timeouts.HttpMs)));
                break;
            case DeviceKind.Printer:
                checks.Add(new TcpPortCheck(host, [device.Port ?? 9100], connect));
                break;
            case DeviceKind.Generic when device.Port is { } p:
                checks.Add(new TcpPortCheck(host, [p], connect));
                break;
        }

        // The main port is already covered above (for generic devices without a port, port is 0).
        var extraPorts = (device.TcpPorts ?? []).Where(p => p != port).Distinct().ToArray();
        if (extraPorts.Length > 0)
            checks.Add(new TcpPortCheck(host, extraPorts, connect));

        if (device.Http is { } http)
        {
            checks.Add(new HttpCheck(new Uri(http.Url), http.ExpectStatus, http.ExpectBodyContains,
                http.IgnoreCertificateErrors, TimeSpan.FromMilliseconds(timeouts.HttpMs)));
        }

        if (!string.IsNullOrWhiteSpace(device.WindowsQueue) && services.PrintQueues is { } queues)
            checks.Add(new PrintQueueCheck(device.WindowsQueue, queues));

        return new DeviceProbe(checks);
    }
}
