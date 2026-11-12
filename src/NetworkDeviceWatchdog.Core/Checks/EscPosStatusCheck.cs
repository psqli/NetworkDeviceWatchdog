using System.Globalization;
using NetworkDeviceWatchdog.Localization;

namespace NetworkDeviceWatchdog.Checks;

/// <summary>
/// Queries an ESC/POS printer with the real-time status command
/// <c>DLE EOT n</c> (<c>10 04 n</c>) over its raw TCP port.
/// </summary>
/// <remarks>
/// Fails with <see cref="FailureCode.PortBlocked"/> when the port refuses the
/// connection (typically held by a stale client: these printers accept a single
/// connection), <see cref="FailureCode.NoResponse"/> when it connects but does not
/// answer, and <see cref="FailureCode.DeviceError"/> when the printer reports an error.
/// The socket is always closed so the printer's single slot is released.
/// </remarks>
public sealed class EscPosStatusCheck(string host, int port, TimeSpan connectTimeout, TimeSpan readTimeout) : IDeviceCheck
{
    public string Name => $"escpos:{port}";

    public async Task<CheckResult> RunAsync(CancellationToken cancellationToken)
    {
        var (socket, error) = await Net.ConnectAsync(host, port, connectTimeout, cancellationToken).ConfigureAwait(false);
        if (socket is null)
            return CheckResult.Fail(FailureCode.PortBlocked, TcpPortCheck.PortBlocked(host, port, error!, rawPrintHint: true));

        using (socket)
        {
            // Some printers push automatic status packets right after connecting.
            await Net.DrainAsync(socket, TimeSpan.FromMilliseconds(50), cancellationToken).ConfigureAwait(false);

            var s1 = await QueryAsync(socket, 1, cancellationToken).ConfigureAwait(false);
            if (s1 < 0 || !EscPosStatus.IsValidReply(s1))
            {
                var detail = s1 < 0
                    ? new Msg("detail.noReply")
                    : Msg.Of("detail.unexpectedReply", ("value", s1.ToString("X2", CultureInfo.InvariantCulture)));
                return CheckResult.Fail(FailureCode.NoResponse, Msg.Of("msg.noResponse.escpos", ("port", port), ("detail", detail)));
            }

            var s4 = await QueryAsync(socket, 4, cancellationToken).ConfigureAwait(false);
            int s2 = -1, s3 = -1;
            if (EscPosStatus.IsOffline(s1))
            {
                s2 = await QueryAsync(socket, 2, cancellationToken).ConfigureAwait(false);
                s3 = await QueryAsync(socket, 3, cancellationToken).ConfigureAwait(false);
            }

            var status = EscPosStatus.Interpret(s1, s2, s3, s4);
            if (status.Errors.Count > 0)
            {
                return CheckResult
                    .Fail(FailureCode.DeviceError, Msg.Of("msg.deviceError", ("reasons", new MsgList([.. status.Errors]))))
                    .WithNotes(status.Warnings);
            }
            return CheckResult.Ok(status.Warnings);
        }
    }

    private async Task<int> QueryAsync(System.Net.Sockets.Socket socket, byte n, CancellationToken cancellationToken)
    {
        await socket.SendAsync(new byte[] { 0x10, 0x04, n }, System.Net.Sockets.SocketFlags.None, cancellationToken).ConfigureAwait(false);
        return await Net.ReadByteAsync(socket, readTimeout, cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>Decodes ESC/POS <c>DLE EOT</c> status bytes (Epson-compatible).</summary>
public static class EscPosStatus
{
    /// <summary>Decoded status: errors make the device fail; warnings are informational.</summary>
    public sealed record Result(IReadOnlyList<Msg> Errors, IReadOnlyList<Msg> Warnings);

    /// <summary>Every status byte has bit 1 and bit 4 set and bits 0 and 7 clear.</summary>
    public static bool IsValidReply(int value) => value >= 0 && (value & 0x93) == 0x12;

    /// <summary>Printer status (n=1), bit 3: offline.</summary>
    public static bool IsOffline(int printerStatus) => (printerStatus & 0x08) != 0;

    /// <summary>
    /// Interprets the replies to n=1 (printer), n=2 (offline cause), n=3 (error cause)
    /// and n=4 (paper sensors). Pass -1 for bytes that were not read.
    /// </summary>
    public static Result Interpret(int printer, int offlineCause, int errorCause, int paperSensor)
    {
        var errors = new List<Msg>();
        var warnings = new List<Msg>();

        if (IsValidReply(paperSensor))
        {
            if ((paperSensor & 0x60) != 0)
                errors.Add(new Msg("reason.paperEnd"));
            else if ((paperSensor & 0x0C) != 0)
                warnings.Add(new Msg("reason.paperNearEnd"));
        }

        if (IsValidReply(printer) && IsOffline(printer))
        {
            var before = errors.Count;
            if (IsValidReply(offlineCause))
            {
                if ((offlineCause & 0x04) != 0) errors.Add(new Msg("reason.coverOpen"));
                if ((offlineCause & 0x08) != 0) errors.Add(new Msg("reason.feedButton"));
                if ((offlineCause & 0x20) != 0 && !errors.Any(e => e.Key == "reason.paperEnd"))
                    errors.Add(new Msg("reason.stoppedPaperEnd"));
                if ((offlineCause & 0x40) != 0) errors.Add(new Msg("reason.errorState"));
            }
            if (IsValidReply(errorCause))
            {
                if ((errorCause & 0x08) != 0) errors.Add(new Msg("reason.cutterError"));
                if ((errorCause & 0x20) != 0) errors.Add(new Msg("reason.unrecoverable"));
                if ((errorCause & 0x40) != 0) errors.Add(new Msg("reason.overheated"));
            }
            if (errors.Count == before && !errors.Any(e => e.Key == "reason.paperEnd"))
                errors.Add(new Msg("reason.offlineUnknown"));
        }
        return new Result(errors, warnings);
    }
}
