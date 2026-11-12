using NetworkDeviceWatchdog.Localization;

namespace NetworkDeviceWatchdog.Checks;

/// <summary>Outcome of one check.</summary>
public enum CheckOutcome
{
    Ok,

    /// <summary>The device is legitimately busy (this computer is printing to it); the round is skipped.</summary>
    Busy,

    Failed,
}

/// <summary>What kind of failure was found. Each code has a localized title (<c>failure.*</c>).</summary>
public enum FailureCode
{
    /// <summary>No ping reply.</summary>
    Unreachable,

    /// <summary>The IP address answers with another device's MAC address.</summary>
    IpConflict,

    /// <summary>Ping works but a TCP port refuses or ignores connections.</summary>
    PortBlocked,

    /// <summary>The port accepts connections but the device does not answer its protocol.</summary>
    NoResponse,

    /// <summary>The device answers and reports an error (paper out, cover open, ...).</summary>
    DeviceError,

    /// <summary>A job in the Windows print queue is in an error state.</summary>
    QueueError,

    /// <summary>This computer's print connection stayed open for too long.</summary>
    JobStuck,

    /// <summary>The HTTP health check returned the wrong status or content.</summary>
    HealthCheckFailed,

    /// <summary>The check could not run (unexpected exception).</summary>
    CheckFailed,
}

public static class FailureCodes
{
    /// <summary>Localization key of the short failure title.</summary>
    public static string TitleKey(this FailureCode code) => code switch
    {
        FailureCode.Unreachable => "failure.unreachable",
        FailureCode.IpConflict => "failure.ipConflict",
        FailureCode.PortBlocked => "failure.portBlocked",
        FailureCode.NoResponse => "failure.noResponse",
        FailureCode.DeviceError => "failure.deviceError",
        FailureCode.QueueError => "failure.queueError",
        FailureCode.JobStuck => "failure.jobStuck",
        FailureCode.HealthCheckFailed => "failure.healthCheckFailed",
        _ => "failure.checkFailed",
    };

    /// <summary>Short title as a message.</summary>
    public static Msg Title(this FailureCode code) => new(code.TitleKey());
}

/// <summary>Result of a check or of a whole device probe.</summary>
public sealed record CheckResult
{
    private CheckResult(CheckOutcome outcome, FailureCode? failure, Msg? message, IReadOnlyList<Msg> notes)
    {
        Outcome = outcome;
        Failure = failure;
        Message = message;
        Notes = notes;
    }

    public CheckOutcome Outcome { get; }

    /// <summary>Set when <see cref="Outcome"/> is <see cref="CheckOutcome.Failed"/>.</summary>
    public FailureCode? Failure { get; }

    /// <summary>Explanation of the failure.</summary>
    public Msg? Message { get; }

    /// <summary>Warnings that are not failures (e.g. paper running low).</summary>
    public IReadOnlyList<Msg> Notes { get; }

    public static CheckResult Ok(params IReadOnlyList<Msg> notes) => new(CheckOutcome.Ok, null, null, notes);

    public static CheckResult Busy() => new(CheckOutcome.Busy, null, null, []);

    public static CheckResult Fail(FailureCode code, Msg message) => new(CheckOutcome.Failed, code, message, []);

    /// <summary>Returns a copy with extra notes.</summary>
    public CheckResult WithNotes(IReadOnlyList<Msg> notes) =>
        notes.Count == 0 ? this : new CheckResult(Outcome, Failure, Message, [.. notes, .. Notes]);
}

/// <summary>One check of a device (ping, a TCP port, a status query...).</summary>
public interface IDeviceCheck
{
    /// <summary>Short name for diagnostics, e.g. <c>ping</c>, <c>escpos:9100</c>.</summary>
    string Name { get; }

    Task<CheckResult> RunAsync(CancellationToken cancellationToken);
}
