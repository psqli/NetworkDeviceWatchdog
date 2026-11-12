using System.Buffers.Binary;
using System.Net;
using System.Net.NetworkInformation;
using System.Text;
using NetworkDeviceWatchdog.Checks;
using NetworkDeviceWatchdog.Localization;
using NetworkDeviceWatchdog.Platform;

namespace NetworkDeviceWatchdog.Tests;

public class IppTests
{
    private static readonly Localizer En = Localizer.Create("en");

    /// <summary>Builds an IPP response with a printer-state and printer-state-reasons.</summary>
    internal static byte[] Response(ushort status, int printerState, params string[] reasons)
    {
        using var ms = new MemoryStream();
        void U16(int v) { Span<byte> b = stackalloc byte[2]; BinaryPrimitives.WriteUInt16BigEndian(b, (ushort)v); ms.Write(b); }
        void Attr(byte tag, string name, byte[] value)
        {
            var n = Encoding.UTF8.GetBytes(name);
            ms.WriteByte(tag); U16(n.Length); ms.Write(n); U16(value.Length); ms.Write(value);
        }
        ms.Write([0x02, 0x00]);
        U16(status);
        ms.Write([0, 0, 0, 1]);
        ms.WriteByte(0x01);
        Attr(0x47, "attributes-charset", "utf-8"u8.ToArray());
        ms.WriteByte(0x04);
        var state = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(state, printerState);
        Attr(0x23, "printer-state", state);
        for (var i = 0; i < reasons.Length; i++)
            Attr(0x44, i == 0 ? "printer-state-reasons" : "", Encoding.UTF8.GetBytes(reasons[i]));
        ms.WriteByte(0x03);
        return ms.ToArray();
    }

    private static IppCheck Check(StubHandler handler) =>
        new("printer.local", 631, "/ipp/print", tls: false, TimeSpan.FromSeconds(2), handler);

    private static StubHandler Answer(byte[] body, HttpStatusCode code = HttpStatusCode.OK) =>
        new(_ => Task.FromResult(new HttpResponseMessage(code) { Content = new ByteArrayContent(body) }));

    [Fact]
    public void Request_is_a_well_formed_get_printer_attributes()
    {
        var request = IppMessage.BuildGetPrinterState("ipp://printer.local:631/ipp/print", 7);
        Assert.Equal(0x02, request[0]);
        Assert.Equal(IppMessage.GetPrinterAttributes, BinaryPrimitives.ReadUInt16BigEndian(request.AsSpan(2)));
        Assert.Equal(7, BinaryPrimitives.ReadInt32BigEndian(request.AsSpan(4)));
        Assert.Equal(0x03, request[^1]);

        // The request parses with the same decoder (same wire format as responses).
        var parsed = IppMessage.Parse(request);
        Assert.Equal("ipp://printer.local:631/ipp/print", parsed.Attributes["printer-uri"][0]);
        Assert.Equal(new object[] { "printer-state", "printer-state-reasons", "printer-state-message" },
            parsed.Attributes["requested-attributes"].ToArray());
    }

    [Fact]
    public void Parses_state_and_multi_valued_reasons()
    {
        var r = IppMessage.Parse(Response(0x0000, 5, "media-empty-error", "toner-low-warning"));
        Assert.True(r.IsSuccess);
        Assert.Equal(5, r.PrinterState);
        Assert.Equal(new[] { "media-empty-error", "toner-low-warning" }, r.StateReasons.ToArray());
    }

    [Fact]
    public void Truncated_response_is_rejected()
    {
        var bytes = Response(0, 3, "none");
        Assert.Throws<FormatException>(() => IppMessage.Parse(bytes.AsSpan(0, bytes.Length - 6)));
    }

    [Fact]
    public void Reasons_are_classified_by_suffix()
    {
        var (errors, warnings) = IppCheck.ClassifyReasons(
            ["none", "media-jam-error", "toner-low-warning", "other-report", "door-open", "spaceship-error"]);
        Assert.Equal(new[] { "paper jam", "door open", "spaceship" }, errors.Select(En.Format).ToArray());
        Assert.Equal("toner or ink low", En.Format(Assert.Single(warnings)));
    }

    [Fact]
    public async Task Idle_printer_is_ok_and_warnings_become_notes()
    {
        var handler = Answer(Response(0, 3, "toner-low-warning"));
        using var check = Check(handler);
        var result = await check.RunAsync(CancellationToken.None);
        Assert.Equal(CheckOutcome.Ok, result.Outcome);
        Assert.Equal("toner or ink low", En.Format(Assert.Single(result.Notes)));

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("http://printer.local:631/ipp/print", request.RequestUri!.ToString());
        Assert.Equal("application/ipp", request.Content!.Headers.ContentType!.MediaType);
    }

    [Fact]
    public async Task Printer_with_errors_is_a_device_error()
    {
        using var check = Check(Answer(Response(0, 5, "media-empty-error", "cover-open-error")));
        var result = await check.RunAsync(CancellationToken.None);
        Assert.Equal(FailureCode.DeviceError, result.Failure);
        Assert.Equal("The device reports: out of paper, cover open.", En.Format(result.Message!));
    }

    [Fact]
    public async Task Stopped_printer_without_reasons_is_still_an_error()
    {
        using var check = Check(Answer(Response(0, 5, "none")));
        var result = await check.RunAsync(CancellationToken.None);
        Assert.Equal("The device reports: printer stopped.", En.Format(result.Message!));
    }

    [Fact]
    public async Task Http_and_ipp_error_statuses_are_no_response()
    {
        using var http = Check(Answer([], HttpStatusCode.NotFound));
        Assert.Contains("HTTP status 404", En.Format((await http.RunAsync(CancellationToken.None)).Message!));

        using var ipp = Check(Answer(Response(0x0406, 3)));
        Assert.Contains("IPP status 0x0406", En.Format((await ipp.RunAsync(CancellationToken.None)).Message!));

        using var junk = Check(Answer([1, 2, 3]));
        var result = await junk.RunAsync(CancellationToken.None);
        Assert.Equal(FailureCode.NoResponse, result.Failure);
        Assert.Contains("invalid response", En.Format(result.Message!));
    }
}

public class HttpCheckTests
{
    private static readonly Localizer En = Localizer.Create("en");

    private static HttpCheck Check(HttpStatusCode code, string body, int? expect = null, string? contains = null) =>
        new(new Uri("http://device.local/health"), expect, contains, false, TimeSpan.FromSeconds(2),
            new StubHandler(_ => Task.FromResult(new HttpResponseMessage(code) { Content = new StringContent(body) })));

    [Fact]
    public async Task Any_2xx_passes_by_default()
    {
        using var check = Check(HttpStatusCode.NoContent, "");
        Assert.Equal(CheckOutcome.Ok, (await check.RunAsync(CancellationToken.None)).Outcome);
    }

    [Fact]
    public async Task Wrong_status_fails()
    {
        using var check = Check(HttpStatusCode.ServiceUnavailable, "");
        var result = await check.RunAsync(CancellationToken.None);
        Assert.Equal(FailureCode.HealthCheckFailed, result.Failure);
        Assert.Equal("http://device.local/health answered HTTP 503, expected 2xx.", En.Format(result.Message!));
    }

    [Fact]
    public async Task Exact_status_can_be_required()
    {
        using var ok = Check(HttpStatusCode.Found, "", expect: 302);
        Assert.Equal(CheckOutcome.Ok, (await ok.RunAsync(CancellationToken.None)).Outcome);
        using var wrong = Check(HttpStatusCode.OK, "", expect: 302);
        Assert.Contains("expected 302", En.Format((await wrong.RunAsync(CancellationToken.None)).Message!));
    }

    [Fact]
    public async Task Body_must_contain_the_expected_text()
    {
        using var ok = Check(HttpStatusCode.OK, "{\"status\":\"ok\"}", contains: "\"ok\"");
        Assert.Equal(CheckOutcome.Ok, (await ok.RunAsync(CancellationToken.None)).Outcome);
        using var bad = Check(HttpStatusCode.OK, "{\"status\":\"degraded\"}", contains: "\"ok\"");
        Assert.Equal(FailureCode.HealthCheckFailed, (await bad.RunAsync(CancellationToken.None)).Failure);
    }

    [Fact]
    public async Task Connection_refused_is_port_blocked()
    {
        using var check = new HttpCheck(new Uri($"http://127.0.0.1:{Ports.Closed()}/"), null, null, false, TimeSpan.FromSeconds(10));
        var result = await check.RunAsync(CancellationToken.None);
        Assert.Equal(FailureCode.PortBlocked, result.Failure);
        Assert.Contains("connection refused", En.Format(result.Message!));
    }

    [Fact]
    public async Task Slow_device_is_not_responding()
    {
        var handler = new StubHandler(async _ =>
        {
            await Task.Delay(TimeSpan.FromSeconds(5));
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        using var check = new HttpCheck(new Uri("http://device.local/"), null, null, false, TimeSpan.FromMilliseconds(200), handler);
        var result = await check.RunAsync(CancellationToken.None);
        Assert.Equal(FailureCode.NoResponse, result.Failure);
    }
}

public class NetworkCheckTests
{
    private const string ArpTable = """
        IP address       HW type     Flags       HW address            Mask     Device
        192.168.10.21    0x1         0x2         00:11:22:33:44:55     *        eth0
        192.168.10.22    0x1         0x0         00:00:00:00:00:00     *        eth0
        """;

    [Fact]
    public void Linux_arp_table_is_parsed()
    {
        Assert.Equal(PhysicalAddress.Parse("001122334455"),
            SystemArpResolver.ParseProcNetArp(ArpTable, IPAddress.Parse("192.168.10.21")));
        Assert.Null(SystemArpResolver.ParseProcNetArp(ArpTable, IPAddress.Parse("192.168.10.22")));   // incomplete
        Assert.Null(SystemArpResolver.ParseProcNetArp(ArpTable, IPAddress.Parse("192.168.10.99")));
        Assert.Null(SystemArpResolver.ParseProcNetArp(null, IPAddress.Parse("192.168.10.21")));
    }

    [Fact]
    public async Task Mac_mismatch_is_an_ip_conflict()
    {
        var expected = PhysicalAddress.Parse("001122334455");
        var other = new FixedArp(PhysicalAddress.Parse("AABBCCDDEEFF"));
        var result = await new MacCheck("192.168.10.21", expected, other).RunAsync(CancellationToken.None);
        Assert.Equal(FailureCode.IpConflict, result.Failure);
        Assert.Equal(
            "192.168.10.21 is answered by MAC address AA-BB-CC-DD-EE-FF, but the device has 00-11-22-33-44-55. Another device is using its IP address.",
            Localizer.Create("en").Format(result.Message!));

        Assert.Equal(CheckOutcome.Ok, (await new MacCheck("192.168.10.21", expected, new FixedArp(expected)).RunAsync(CancellationToken.None)).Outcome);
        Assert.Equal(CheckOutcome.Ok, (await new MacCheck("192.168.10.21", expected, new FixedArp(null)).RunAsync(CancellationToken.None)).Outcome);
    }

    [Fact]
    public async Task Open_print_connection_from_this_computer_means_busy()
    {
        var busy = new LocalConnectionBusyCheck("192.168.10.21", 9100, new FixedConnections(true));
        Assert.Equal(CheckOutcome.Busy, (await busy.RunAsync(CancellationToken.None)).Outcome);
        var idle = new LocalConnectionBusyCheck("192.168.10.21", 9100, new FixedConnections(false));
        Assert.Equal(CheckOutcome.Ok, (await idle.RunAsync(CancellationToken.None)).Outcome);
    }

    [Fact]
    public async Task System_connection_table_sees_a_real_connection()
    {
        await using var printer = new FakeEscPosPrinter(FakePrinterMode.Silent);
        using var client = new System.Net.Sockets.TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, printer.Port);
        Assert.True(new SystemConnectionTable().HasEstablishedConnection([IPAddress.Loopback], printer.Port));
        Assert.False(new SystemConnectionTable().HasEstablishedConnection([IPAddress.Loopback], Ports.Closed()));
    }

    [Fact]
    public async Task Tcp_port_check()
    {
        await using var printer = new FakeEscPosPrinter(FakePrinterMode.Ready);
        var timeout = TimeSpan.FromSeconds(10);   // Windows refuses closed loopback ports only after ~2 s
        Assert.Equal(CheckOutcome.Ok, (await new TcpPortCheck("127.0.0.1", [printer.Port], timeout).RunAsync(CancellationToken.None)).Outcome);
        var closed = Ports.Closed();
        var result = await new TcpPortCheck("127.0.0.1", [printer.Port, closed], timeout).RunAsync(CancellationToken.None);
        Assert.Equal(FailureCode.PortBlocked, result.Failure);
        Assert.Contains($"TCP port {closed}", Localizer.Create("en").Format(result.Message!));
    }

    private sealed class FixedArp(PhysicalAddress? mac) : IArpResolver
    {
        public PhysicalAddress? Resolve(IPAddress address) => mac;
    }

    private sealed class FixedConnections(bool open) : IConnectionTable
    {
        public bool HasEstablishedConnection(IReadOnlyCollection<IPAddress> addresses, int port) => open;
    }
}
