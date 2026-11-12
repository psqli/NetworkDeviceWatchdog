using NetworkDeviceWatchdog.Checks;
using NetworkDeviceWatchdog.Configuration;
using NetworkDeviceWatchdog.Localization;
using NetworkDeviceWatchdog.Monitoring;
using NetworkDeviceWatchdog.Notifications;

namespace NetworkDeviceWatchdog.Tests;

/// <summary>A check whose result the test sets before each round.</summary>
internal sealed class ScriptedCheck(string name = "scripted") : IDeviceCheck
{
    public CheckResult Next { get; set; } = CheckResult.Ok();

    public int Runs { get; private set; }

    public string Name => name;

    public Task<CheckResult> RunAsync(CancellationToken cancellationToken)
    {
        Runs++;
        return Task.FromResult(Next);
    }
}

public class DeviceMonitorTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 12, 12, 0, 0, TimeSpan.Zero);
    private static readonly CheckResult Down = CheckResult.Fail(FailureCode.Unreachable, Msg.Of("msg.unreachable", ("host", "h")));
    private static readonly CheckResult Blocked = CheckResult.Fail(FailureCode.PortBlocked, new Msg("detail.refused"));

    private static DeviceMonitor Monitor(int fail = 3, int busy = 10) =>
        new("Kitchen", "10.0.0.22", new DeviceProbe([]), new MonitorThresholds(fail, busy));

    private static List<MonitorEvent> Run(DeviceMonitor m, params CheckResult[] results) =>
        [.. results.SelectMany((r, i) => m.Apply(r, T0.AddMinutes(i)))];

    [Fact]
    public void Alerts_only_after_the_threshold_and_only_once()
    {
        var m = Monitor(fail: 3);
        var events = Run(m, Down, Down, Down, Down, Down);
        Assert.IsType<FailureObserved>(events[0]);
        var alert = Assert.IsType<AlertRaised>(Assert.Single(events, e => e is AlertRaised));
        Assert.Equal(FailureCode.Unreachable, alert.Failure);
        Assert.Equal(T0.AddMinutes(2), alert.Time);
        Assert.Equal(2, events.Count);
        Assert.True(m.Snapshot.Alerted);
        Assert.Equal(DeviceHealth.Problem, m.Snapshot.Health);
    }

    [Fact]
    public void Recovery_is_reported_after_an_alert()
    {
        var m = Monitor(fail: 2);
        var events = Run(m, Down, Down, CheckResult.Ok());
        var recovered = Assert.IsType<Recovered>(events[^1]);
        Assert.Equal(FailureCode.Unreachable, recovered.PreviousFailure);
        Assert.Equal(DeviceHealth.Ok, m.Snapshot.Health);
        Assert.False(m.Snapshot.Alerted);
    }

    [Fact]
    public void Short_glitches_are_logged_but_not_alerted()
    {
        var m = Monitor(fail: 3);
        var events = Run(m, Down, Down, CheckResult.Ok());
        Assert.DoesNotContain(events, e => e is AlertRaised or Recovered);
        var cleared = Assert.IsType<TransientCleared>(events[^1]);
        Assert.Equal(2, cleared.Checks);
    }

    [Fact]
    public void A_changing_failure_keeps_counting_and_logs_each_new_code()
    {
        var m = Monitor(fail: 3);
        var events = Run(m, Down, Blocked, Blocked);
        Assert.Equal(2, events.OfType<FailureObserved>().Count());
        Assert.Equal(FailureCode.PortBlocked, Assert.Single(events.OfType<AlertRaised>()).Failure);
    }

    [Fact]
    public void Busy_rounds_are_skipped()
    {
        var m = Monitor(fail: 1, busy: 10);
        var events = Run(m, CheckResult.Busy(), CheckResult.Busy(), CheckResult.Ok());
        Assert.Empty(events);
        Assert.Equal(DeviceHealth.Ok, m.Snapshot.Health);
    }

    [Fact]
    public void A_print_connection_open_too_long_becomes_a_stuck_job_alert()
    {
        var m = Monitor(fail: 3, busy: 4);
        var events = Run(m, CheckResult.Busy(), CheckResult.Busy(), CheckResult.Busy(), CheckResult.Busy());
        var alert = Assert.Single(events.OfType<AlertRaised>());
        Assert.Equal(FailureCode.JobStuck, alert.Failure);
        Assert.Contains("for 4 checks", Localizer.Create("en").Format(alert.Message));

        // Still stuck: no second alert. Then the job finishes: recovery.
        Assert.Empty(m.Apply(CheckResult.Busy(), T0.AddMinutes(10)));
        Assert.IsType<Recovered>(Assert.Single(m.Apply(CheckResult.Ok(), T0.AddMinutes(11))));
    }

    [Fact]
    public void Notes_are_kept_in_the_snapshot()
    {
        var m = Monitor();
        m.Apply(CheckResult.Ok(new Msg("reason.paperNearEnd")), T0);
        Assert.Equal("reason.paperNearEnd", Assert.Single(m.Snapshot.Notes).Key);
    }
}

public class DeviceProbeTests
{
    [Fact]
    public async Task First_failure_wins_and_later_checks_do_not_run()
    {
        var ping = new ScriptedCheck("ping") { Next = CheckResult.Fail(FailureCode.Unreachable, new Msg("x")) };
        var status = new ScriptedCheck("status");
        using var probe = new DeviceProbe([ping, status]);
        var result = await probe.RunAsync(CancellationToken.None);
        Assert.Equal(FailureCode.Unreachable, result.Failure);
        Assert.Equal(0, status.Runs);
    }

    [Fact]
    public async Task Notes_from_passing_checks_are_combined()
    {
        using var probe = new DeviceProbe([
            new ScriptedCheck { Next = CheckResult.Ok(new Msg("reason.paperNearEnd")) },
            new ScriptedCheck { Next = CheckResult.Ok(new Msg("reason.tonerLow")) },
        ]);
        var result = await probe.RunAsync(CancellationToken.None);
        Assert.Equal(new[] { "reason.paperNearEnd", "reason.tonerLow" }, result.Notes.Select(n => n.Key).ToArray());
    }

    [Fact]
    public async Task A_crashing_check_becomes_check_failed()
    {
        using var probe = new DeviceProbe([new ThrowingCheck()]);
        var result = await probe.RunAsync(CancellationToken.None);
        Assert.Equal(FailureCode.CheckFailed, result.Failure);
        Assert.Contains("boom", Localizer.Create("en").Format(result.Message!));
    }

    private sealed class ThrowingCheck : IDeviceCheck
    {
        public string Name => "throwing";

        public Task<CheckResult> RunAsync(CancellationToken cancellationToken) => throw new InvalidOperationException("boom");
    }
}

public class WatchdogServiceTests
{
    private sealed class RecordingNotifier(string name = "recording", bool fail = false) : INotifier
    {
        public List<Notification> Received { get; } = [];

        public string Name => name;

        public Task NotifyAsync(Notification notification, CancellationToken cancellationToken)
        {
            Received.Add(notification);
            return fail ? Task.FromException(new HttpRequestException("offline")) : Task.CompletedTask;
        }
    }

    private sealed class MemoryLog : ILogSink
    {
        public List<string> Lines { get; } = [];

        public void Write(DateTimeOffset time, string message) => Lines.Add(message);
    }

    private static (WatchdogService Service, ScriptedCheck Check, RecordingNotifier Notifier, MemoryLog Log) Create(
        string language, bool failingExtraNotifier = false)
    {
        var config = ConfigLoader.Parse("""{ "failThreshold": 2, "devices": [ { "name": "Cozinha", "host": "10.0.0.22" } ] }""");
        var check = new ScriptedCheck();
        var monitor = new DeviceMonitor("Cozinha", "10.0.0.22", new DeviceProbe([check]), new MonitorThresholds(2, 10));
        var notifier = new RecordingNotifier();
        INotifier[] notifiers = failingExtraNotifier ? [new RecordingNotifier("ntfy", fail: true), notifier] : [notifier];
        var log = new MemoryLog();
        var service = new WatchdogService(config, [monitor], notifiers, Localizer.Create(language), log);
        return (service, check, notifier, log);
    }

    [Fact]
    public async Task Alert_and_recovery_are_localized()
    {
        var (service, check, notifier, _) = Create("pt");
        check.Next = CheckResult.Fail(FailureCode.NoResponse, Msg.Of("msg.noResponse.escpos", ("port", 9100), ("detail", new Msg("detail.noReply"))));
        await service.CheckAllAsync(CancellationToken.None);
        Assert.Empty(notifier.Received);
        await service.CheckAllAsync(CancellationToken.None);

        var alert = Assert.Single(notifier.Received);
        Assert.Equal(NotificationKind.Problem, alert.Kind);
        Assert.Equal("Cozinha: Não responde", alert.Title);
        Assert.StartsWith("A porta TCP 9100 aceita a conexão", alert.Body);

        check.Next = CheckResult.Ok();
        await service.CheckAllAsync(CancellationToken.None);
        var recovery = notifier.Received[^1];
        Assert.Equal(NotificationKind.Recovery, recovery.Kind);
        Assert.Equal("Cozinha voltou a funcionar", recovery.Title);
        Assert.Equal("10.0.0.22 está funcionando de novo (problema anterior: Não responde).", recovery.Body);
    }

    [Fact]
    public async Task A_failing_channel_does_not_block_the_others()
    {
        var (service, check, notifier, log) = Create("en", failingExtraNotifier: true);
        check.Next = CheckResult.Fail(FailureCode.Unreachable, Msg.Of("msg.unreachable", ("host", "10.0.0.22")));
        await service.CheckAllAsync(CancellationToken.None);
        await service.CheckAllAsync(CancellationToken.None);
        Assert.Single(notifier.Received);
        Assert.Contains(log.Lines, l => l.StartsWith("Could not send the alert through ntfy: offline", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Status_event_reports_every_device()
    {
        var (service, _, _, _) = Create("en");
        IReadOnlyList<DeviceSnapshot>? seen = null;
        service.StatusChanged += s => seen = s;
        await service.CheckAllAsync(CancellationToken.None);
        Assert.Equal(DeviceHealth.Ok, Assert.Single(seen!).Health);
    }

    [Fact]
    public async Task Test_alert_goes_through_every_channel_in_the_configured_language()
    {
        var (service, _, notifier, _) = Create("zh");
        await service.SendTestAlertAsync(CancellationToken.None);
        var n = Assert.Single(notifier.Received);
        Assert.Equal(NotificationKind.Test, n.Kind);
        Assert.Equal("测试：端口被占用", n.Title);
    }

    [Fact]
    public async Task Runs_rounds_on_the_interval_until_cancelled()
    {
        var (service, check, _, log) = Create("en");
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        await service.RunAsync(cts.Token);
        Assert.Equal(1, check.Runs);   // first round immediately; the 60 s interval never elapsed
        Assert.StartsWith("Watchdog started: 1 device(s)", log.Lines[0]);
        Assert.Equal("Watchdog stopped.", log.Lines[^1]);
    }
}
