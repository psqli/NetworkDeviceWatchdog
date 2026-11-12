using NetworkDeviceWatchdog.Checks;
using NetworkDeviceWatchdog.Localization;

namespace NetworkDeviceWatchdog.Monitoring;

/// <summary>Thresholds that turn individual check results into alerts.</summary>
public sealed record MonitorThresholds(int FailThreshold, int BusyThreshold);

/// <summary>Current state of a device as shown in the tray menu and <c>--once</c>.</summary>
public enum DeviceHealth
{
    Unknown,
    Ok,
    Busy,
    Problem,
}

/// <summary>Snapshot of a device after a check.</summary>
public sealed record DeviceSnapshot(
    string Device,
    string Host,
    DeviceHealth Health,
    FailureCode? Failure,
    Msg? Message,
    IReadOnlyList<Msg> Notes,
    bool Alerted,
    DateTimeOffset? LastCheck);

/// <summary>Something worth logging or notifying.</summary>
public abstract record MonitorEvent(string Device, string Host, DateTimeOffset Time);

/// <summary>A failure was seen (logged once per failure code, before any alert).</summary>
public sealed record FailureObserved(string Device, string Host, DateTimeOffset Time, FailureCode Failure, Msg Message)
    : MonitorEvent(Device, Host, Time);

/// <summary>The failure persisted long enough: notify people.</summary>
public sealed record AlertRaised(string Device, string Host, DateTimeOffset Time, FailureCode Failure, Msg Message)
    : MonitorEvent(Device, Host, Time);

/// <summary>A device that had been alerted is working again.</summary>
public sealed record Recovered(string Device, string Host, DateTimeOffset Time, FailureCode PreviousFailure)
    : MonitorEvent(Device, Host, Time);

/// <summary>A failure cleared before reaching the alert threshold (logged only).</summary>
public sealed record TransientCleared(string Device, string Host, DateTimeOffset Time, FailureCode Failure, int Checks)
    : MonitorEvent(Device, Host, Time);

/// <summary>
/// Tracks one device across check rounds and decides when to alert:
/// <list type="bullet">
/// <item>a failure must repeat <see cref="MonitorThresholds.FailThreshold"/> times in a row;</item>
/// <item>each incident alerts once, and once more when the device recovers;</item>
/// <item>"busy" rounds (this computer is printing) are skipped, but a print connection
/// that stays open for <see cref="MonitorThresholds.BusyThreshold"/> rounds becomes a
/// <see cref="FailureCode.JobStuck"/> alert.</item>
/// </list>
/// </summary>
public sealed class DeviceMonitor(string device, string host, DeviceProbe probe, MonitorThresholds thresholds) : IDisposable
{
    private int _fails;
    private int _busy;
    private bool _alerted;
    private FailureCode? _last;

    public string Device { get; } = device;

    public string Host { get; } = host;

    public DeviceSnapshot Snapshot { get; private set; } =
        new(device, host, DeviceHealth.Unknown, null, null, [], false, null);

    /// <summary>Runs the probe and applies the result.</summary>
    public async Task<IReadOnlyList<MonitorEvent>> CheckAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var result = await probe.RunAsync(cancellationToken).ConfigureAwait(false);
        return Apply(result, now);
    }

    /// <summary>Applies a probe result (pure state transition; used directly by tests).</summary>
    public IReadOnlyList<MonitorEvent> Apply(CheckResult result, DateTimeOffset now)
    {
        var events = new List<MonitorEvent>();

        if (result.Outcome == CheckOutcome.Busy)
        {
            // A receipt prints in seconds; a connection open for many rounds means the
            // printer stopped taking data in the middle of a job.
            _busy++;
            if (_busy < thresholds.BusyThreshold)
            {
                Snapshot = Snapshot with { Health = DeviceHealth.Busy, LastCheck = now };
                return events;
            }
            result = CheckResult.Fail(FailureCode.JobStuck, Msg.Of("msg.jobStuck", ("host", Host), ("count", _busy)));
            _fails = Math.Max(_fails, thresholds.FailThreshold - 1);   // alert on this round
        }
        else
        {
            _busy = 0;
        }

        if (result.Outcome == CheckOutcome.Ok)
        {
            if (_alerted && _last is { } previous)
                events.Add(new Recovered(Device, Host, now, previous));
            else if (_fails > 0 && _last is { } transient)
                events.Add(new TransientCleared(Device, Host, now, transient, _fails));
            _fails = 0;
            _alerted = false;
            _last = null;
            Snapshot = new DeviceSnapshot(Device, Host, DeviceHealth.Ok, null, null, result.Notes, false, now);
            return events;
        }

        var code = result.Failure ?? FailureCode.CheckFailed;
        var message = result.Message ?? code.Title();
        _fails++;
        if (_last != code)
            events.Add(new FailureObserved(Device, Host, now, code, message));
        _last = code;
        if (!_alerted && _fails >= thresholds.FailThreshold)
        {
            events.Add(new AlertRaised(Device, Host, now, code, message));
            _alerted = true;
        }
        Snapshot = new DeviceSnapshot(Device, Host, DeviceHealth.Problem, code, message, result.Notes, _alerted, now);
        return events;
    }

    public void Dispose() => probe.Dispose();
}
