using NetworkDeviceWatchdog.Checks;
using NetworkDeviceWatchdog.Configuration;
using NetworkDeviceWatchdog.Localization;
using NetworkDeviceWatchdog.Notifications;

namespace NetworkDeviceWatchdog.Monitoring;

/// <summary>
/// Checks every device each interval, logs what happens and sends notifications.
/// Devices are checked concurrently; a slow or dead device does not delay the others.
/// </summary>
public sealed class WatchdogService : IDisposable
{
    private readonly IReadOnlyList<DeviceMonitor> _monitors;
    private readonly IReadOnlyList<INotifier> _notifiers;
    private readonly ILogSink _log;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _roundLock = new(1, 1);

    public WatchdogService(
        WatchdogConfig config,
        IReadOnlyList<DeviceMonitor> monitors,
        IReadOnlyList<INotifier> notifiers,
        Localizer localizer,
        ILogSink log,
        TimeProvider? time = null)
    {
        Config = config;
        _monitors = monitors;
        _notifiers = notifiers;
        Localizer = localizer;
        _log = log;
        _time = time ?? TimeProvider.System;
    }

    public WatchdogConfig Config { get; }

    public Localizer Localizer { get; }

    /// <summary>Raised after every round with the state of all devices.</summary>
    public event Action<IReadOnlyList<DeviceSnapshot>>? StatusChanged;

    public IReadOnlyList<DeviceSnapshot> Snapshots => [.. _monitors.Select(m => m.Snapshot)];

    /// <summary>Builds monitors for every enabled device in the configuration.</summary>
    public static WatchdogService Create(
        WatchdogConfig config,
        ProbeServices services,
        IReadOnlyList<INotifier> notifiers,
        Localizer localizer,
        ILogSink log,
        TimeProvider? time = null)
    {
        var thresholds = new MonitorThresholds(config.FailThreshold, config.BusyThreshold);
        var monitors = config.Devices
            .Where(d => d.Enabled)
            .Select(d => new DeviceMonitor(d.Name.Trim(), d.Host.Trim(),
                DeviceProbeFactory.Create(d, config.Timeouts, services), thresholds))
            .ToArray();
        return new WatchdogService(config, monitors, notifiers, localizer, log, time);
    }

    /// <summary>Checks until cancelled.</summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        Log(Localizer.Format("app.started",
            ("count", _monitors.Count), ("interval", Config.IntervalSeconds), ("threshold", Config.FailThreshold)));
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Config.IntervalSeconds), _time);
        try
        {
            do
            {
                await CheckAllAsync(cancellationToken).ConfigureAwait(false);
            }
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        Log(Localizer["app.stopped"]);
    }

    /// <summary>Runs one round of checks (also used by "Check now" and <c>--once</c>).</summary>
    public async Task<IReadOnlyList<DeviceSnapshot>> CheckAllAsync(CancellationToken cancellationToken)
    {
        await _roundLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var now = _time.GetUtcNow();
            var results = await Task.WhenAll(_monitors.Select(m => m.CheckAsync(now, cancellationToken))).ConfigureAwait(false);
            foreach (var ev in results.SelectMany(e => e))
                await HandleAsync(ev, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _roundLock.Release();
        }
        var snapshots = Snapshots;
        StatusChanged?.Invoke(snapshots);
        return snapshots;
    }

    /// <summary>Sends a test notification through every channel.</summary>
    public Task SendTestAlertAsync(CancellationToken cancellationToken)
    {
        var device = Localizer["alert.device.test"];
        var failure = FailureCode.PortBlocked;
        var n = new Notification(NotificationKind.Test, device, "", failure,
            Localizer.Format("alert.title.test", ("device", device), ("failure", failure.Title())),
            Localizer["alert.body.test"], _time.GetUtcNow());
        return NotifyAsync(n, cancellationToken);
    }

    private async Task HandleAsync(MonitorEvent ev, CancellationToken cancellationToken)
    {
        switch (ev)
        {
            case FailureObserved f:
                Log(Localizer.Format("log.problem", ("device", f.Device), ("failure", f.Failure.Title()), ("message", f.Message)));
                break;

            case TransientCleared t:
                Log(Localizer.Format("log.transientCleared", ("device", t.Device), ("failure", t.Failure.Title()), ("count", t.Checks)));
                break;

            case AlertRaised a:
                await NotifyAsync(new Notification(NotificationKind.Problem, a.Device, a.Host, a.Failure,
                    Localizer.Format("alert.title.problem", ("device", a.Device), ("failure", a.Failure.Title())),
                    Localizer.Format(a.Message), a.Time), cancellationToken).ConfigureAwait(false);
                break;

            case Recovered r when Config.Notifications.NotifyRecovery:
                await NotifyAsync(new Notification(NotificationKind.Recovery, r.Device, r.Host, r.PreviousFailure,
                    Localizer.Format("alert.title.recovered", ("device", r.Device)),
                    Localizer.Format("alert.body.recovered", ("host", r.Host), ("failure", r.PreviousFailure.Title())),
                    r.Time), cancellationToken).ConfigureAwait(false);
                break;

            case Recovered r:
                Log(Localizer.Format("alert.body.recovered", ("host", r.Host), ("failure", r.PreviousFailure.Title())));
                break;
        }
    }

    private async Task NotifyAsync(Notification n, CancellationToken cancellationToken)
    {
        Log(Localizer.Format("log.alert", ("title", n.Title), ("body", n.Body)));
        // Notifiers run concurrently and independently: one failing channel (e.g. no
        // internet for ntfy) must not prevent the alert window from showing.
        await Task.WhenAll(_notifiers.Select(async notifier =>
        {
            try
            {
                await notifier.NotifyAsync(n, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception e) when (e is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                Log(Localizer.Format("log.notifierFailed", ("notifier", notifier.Name), ("error", e.Message)));
            }
        })).ConfigureAwait(false);
    }

    private void Log(string message) => _log.Write(_time.GetUtcNow(), message);

    public void Dispose()
    {
        foreach (var m in _monitors)
            m.Dispose();
        _roundLock.Dispose();
    }
}
