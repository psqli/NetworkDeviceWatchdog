using NetworkDeviceWatchdog.Checks;
using NetworkDeviceWatchdog.Localization;

namespace NetworkDeviceWatchdog.Tests;

public class EscPosTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMilliseconds(500);
    private static readonly Localizer En = Localizer.Create("en");

    private static Task<CheckResult> Probe(int port) =>
        new EscPosStatusCheck("127.0.0.1", port, Timeout, Timeout).RunAsync(CancellationToken.None);

    [Theory]
    [InlineData(0x12, true)]
    [InlineData(0x16, true)]
    [InlineData(0x1E, true)]
    [InlineData(0x10, false)]   // ASB packet header byte
    [InlineData(0x13, false)]   // bit 0 set
    [InlineData(0x92, false)]   // bit 7 set
    [InlineData(0xFF, false)]
    [InlineData(-1, false)]
    public void Valid_status_bytes(int value, bool valid)
    {
        Assert.Equal(valid, EscPosStatus.IsValidReply(value));
    }

    [Fact]
    public void Interpret_ready_printer()
    {
        var r = EscPosStatus.Interpret(0x16, -1, -1, 0x12);
        Assert.Empty(r.Errors);
        Assert.Empty(r.Warnings);
    }

    [Fact]
    public void Interpret_paper_near_end_is_only_a_warning()
    {
        var r = EscPosStatus.Interpret(0x16, -1, -1, 0x12 | 0x0C);
        Assert.Empty(r.Errors);
        Assert.Equal("reason.paperNearEnd", Assert.Single(r.Warnings).Key);
    }

    [Fact]
    public void Interpret_offline_causes()
    {
        var r = EscPosStatus.Interpret(0x1E, 0x12 | 0x04 | 0x40, 0x12 | 0x08 | 0x40, 0x12);
        Assert.Equal(new[] { "reason.coverOpen", "reason.errorState", "reason.cutterError", "reason.overheated" },
            r.Errors.Select(e => e.Key).ToArray());
    }

    [Fact]
    public void Interpret_paper_out_is_not_reported_twice()
    {
        var r = EscPosStatus.Interpret(0x1E, 0x12 | 0x20, 0x12, 0x12 | 0x60);
        Assert.Equal("reason.paperEnd", Assert.Single(r.Errors).Key);
    }

    [Fact]
    public void Interpret_offline_without_cause()
    {
        var r = EscPosStatus.Interpret(0x1E, 0x12, 0x12, 0x12);
        Assert.Equal("reason.offlineUnknown", Assert.Single(r.Errors).Key);
    }

    [Fact]
    public async Task Ready_printer_is_ok_and_the_connection_is_released()
    {
        await using var printer = new FakeEscPosPrinter(FakePrinterMode.Ready);
        var result = await Probe(printer.Port);
        Assert.Equal(CheckOutcome.Ok, result.Outcome);
        // A second probe must connect again: the first one closed its socket.
        Assert.Equal(CheckOutcome.Ok, (await Probe(printer.Port)).Outcome);
        Assert.Equal(2, printer.Connections);
    }

    [Fact]
    public async Task Paper_low_is_ok_with_a_note()
    {
        await using var printer = new FakeEscPosPrinter(FakePrinterMode.PaperLow);
        var result = await Probe(printer.Port);
        Assert.Equal(CheckOutcome.Ok, result.Outcome);
        Assert.Equal("paper running low", En.Format(Assert.Single(result.Notes)));
    }

    [Theory]
    [InlineData(FakePrinterMode.CoverOpen, "The device reports: cover open.")]
    [InlineData(FakePrinterMode.PaperOut, "The device reports: out of paper.")]
    [InlineData(FakePrinterMode.CutterError, "The device reports: in error state, cutter error.")]
    public async Task Printer_errors_are_reported(FakePrinterMode mode, string expected)
    {
        await using var printer = new FakeEscPosPrinter(mode);
        var result = await Probe(printer.Port);
        Assert.Equal(FailureCode.DeviceError, result.Failure);
        Assert.Equal(expected, En.Format(result.Message!));
    }

    [Fact]
    public async Task Silent_printer_is_not_responding()
    {
        await using var printer = new FakeEscPosPrinter(FakePrinterMode.Silent);
        var result = await Probe(printer.Port);
        Assert.Equal(FailureCode.NoResponse, result.Failure);
        Assert.Contains("(no reply)", En.Format(result.Message!));
    }

    [Fact]
    public async Task Garbage_reply_is_not_responding()
    {
        await using var printer = new FakeEscPosPrinter(FakePrinterMode.Garbage);
        var result = await Probe(printer.Port);
        Assert.Equal(FailureCode.NoResponse, result.Failure);
        Assert.Contains("unexpected reply 0xFF", En.Format(result.Message!));
    }

    [Fact]
    public async Task Automatic_status_packets_sent_on_connect_are_ignored()
    {
        await using var printer = new FakeEscPosPrinter(FakePrinterMode.AutoStatusOnConnect);
        Assert.Equal(CheckOutcome.Ok, (await Probe(printer.Port)).Outcome);
    }

    [Fact]
    public async Task Closed_port_is_port_blocked_with_the_raw_print_hint()
    {
        // Windows retries a refused loopback connection for ~2 s, so allow more time.
        var slow = TimeSpan.FromSeconds(10);
        var result = await new EscPosStatusCheck("127.0.0.1", Ports.Closed(), slow, slow).RunAsync(CancellationToken.None);
        Assert.Equal(FailureCode.PortBlocked, result.Failure);
        var text = En.Format(result.Message!);
        Assert.Contains("connection refused", text);
        Assert.Contains("netstat -ano", text);
    }
}
