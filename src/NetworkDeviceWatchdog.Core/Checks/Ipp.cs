using System.Buffers.Binary;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using NetworkDeviceWatchdog.Localization;

namespace NetworkDeviceWatchdog.Checks;

/// <summary>Minimal encoder/decoder for the IPP binary format (RFC 8010), enough for Get-Printer-Attributes.</summary>
public static class IppMessage
{
    public const ushort GetPrinterAttributes = 0x000B;

    private const byte OperationAttributesTag = 0x01;
    private const byte EndOfAttributesTag = 0x03;
    private const byte TagInteger = 0x21;
    private const byte TagEnum = 0x23;
    private const byte TagUri = 0x45;
    private const byte TagCharset = 0x47;
    private const byte TagNaturalLanguage = 0x48;
    private const byte TagKeyword = 0x44;

    /// <summary>Builds a Get-Printer-Attributes request asking for the printer state.</summary>
    public static byte[] BuildGetPrinterState(string printerUri, int requestId = 1)
    {
        using var ms = new MemoryStream();
        ms.Write([0x02, 0x00]);                                   // version 2.0
        WriteUInt16(ms, GetPrinterAttributes);
        WriteInt32(ms, requestId);
        ms.WriteByte(OperationAttributesTag);
        WriteAttribute(ms, TagCharset, "attributes-charset", "utf-8");
        WriteAttribute(ms, TagNaturalLanguage, "attributes-natural-language", "en");
        WriteAttribute(ms, TagUri, "printer-uri", printerUri);
        WriteAttribute(ms, TagKeyword, "requested-attributes", "printer-state");
        WriteAttribute(ms, TagKeyword, "", "printer-state-reasons");    // additional value
        WriteAttribute(ms, TagKeyword, "", "printer-state-message");
        ms.WriteByte(EndOfAttributesTag);
        return ms.ToArray();
    }

    /// <summary>Parsed response: status code and attributes (all values as strings or ints).</summary>
    public sealed record Response(ushort StatusCode, IReadOnlyDictionary<string, IReadOnlyList<object>> Attributes)
    {
        public bool IsSuccess => StatusCode < 0x0100;

        /// <summary>printer-state: 3 idle, 4 processing, 5 stopped. Null if absent.</summary>
        public int? PrinterState =>
            Attributes.TryGetValue("printer-state", out var v) && v.Count > 0 && v[0] is int i ? i : null;

        public IReadOnlyList<string> StateReasons =>
            Attributes.TryGetValue("printer-state-reasons", out var v) ? [.. v.OfType<string>()] : [];
    }

    /// <summary>Parses an IPP response. Throws <see cref="FormatException"/> on malformed data.</summary>
    public static Response Parse(ReadOnlySpan<byte> data)
    {
        if (data.Length < 9)
            throw new FormatException("IPP response too short.");
        var status = BinaryPrimitives.ReadUInt16BigEndian(data[2..]);
        var attrs = new Dictionary<string, List<object>>(StringComparer.Ordinal);
        var pos = 8;
        string? current = null;
        while (pos < data.Length)
        {
            var tag = data[pos++];
            if (tag == EndOfAttributesTag)
                break;
            if (tag < 0x10)
            {
                current = null;   // a new attribute group starts
                continue;
            }
            var nameLen = ReadLength(data, ref pos);
            var name = Encoding.UTF8.GetString(Slice(data, ref pos, nameLen));
            var valueLen = ReadLength(data, ref pos);
            var value = Slice(data, ref pos, valueLen);
            if (nameLen > 0)
                current = name;
            if (current is null)
                throw new FormatException("IPP additional value without an attribute.");
            if (!attrs.TryGetValue(current, out var list))
                attrs[current] = list = [];
            list.Add(tag is TagInteger or TagEnum && value.Length == 4
                ? BinaryPrimitives.ReadInt32BigEndian(value)
                : Encoding.UTF8.GetString(value));
        }
        return new Response(status, attrs.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<object>)kv.Value, StringComparer.Ordinal));
    }

    private static int ReadLength(ReadOnlySpan<byte> data, ref int pos)
    {
        if (pos + 2 > data.Length)
            throw new FormatException("IPP response truncated.");
        var len = BinaryPrimitives.ReadUInt16BigEndian(data[pos..]);
        pos += 2;
        return len;
    }

    private static ReadOnlySpan<byte> Slice(ReadOnlySpan<byte> data, ref int pos, int length)
    {
        if (pos + length > data.Length)
            throw new FormatException("IPP response truncated.");
        var s = data.Slice(pos, length);
        pos += length;
        return s;
    }

    private static void WriteAttribute(Stream s, byte tag, string name, string value)
    {
        var n = Encoding.UTF8.GetBytes(name);
        var v = Encoding.UTF8.GetBytes(value);
        s.WriteByte(tag);
        WriteUInt16(s, (ushort)n.Length);
        s.Write(n);
        WriteUInt16(s, (ushort)v.Length);
        s.Write(v);
    }

    private static void WriteUInt16(Stream s, ushort value)
    {
        Span<byte> b = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(b, value);
        s.Write(b);
    }

    private static void WriteInt32(Stream s, int value)
    {
        Span<byte> b = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(b, value);
        s.Write(b);
    }
}

/// <summary>
/// Asks an office printer for its state over IPP (port 631). Fails with
/// <see cref="FailureCode.DeviceError"/> when the printer is stopped or reports
/// an error reason (paper out, jam, toner empty, door open...).
/// </summary>
public sealed class IppCheck : IDeviceCheck, IDisposable
{
    private readonly HttpClient _http;
    private readonly Uri _endpoint;
    private readonly string _printerUri;
    private readonly string _host;
    private readonly int _port;

    public IppCheck(string host, int port, string path, bool tls, TimeSpan timeout, HttpMessageHandler? handler = null)
    {
        _host = host;
        _port = port;
        var hostPart = host.Contains(':', StringComparison.Ordinal) ? $"[{host}]" : host;   // IPv6 literal
        _endpoint = new Uri($"{(tls ? "https" : "http")}://{hostPart}:{port}{path}");
        _printerUri = $"{(tls ? "ipps" : "ipp")}://{hostPart}:{port}{path}";
        _http = new HttpClient(handler ?? CreateHandler(tls, timeout), disposeHandler: true) { Timeout = timeout };
    }

    public string Name => $"ipp:{_port}";

    public async Task<CheckResult> RunAsync(CancellationToken cancellationToken)
    {
        IppMessage.Response response;
        try
        {
            using var content = new ByteArrayContent(IppMessage.BuildGetPrinterState(_printerUri));
            content.Headers.ContentType = new MediaTypeHeaderValue("application/ipp");
            using var reply = await _http.PostAsync(_endpoint, content, cancellationToken).ConfigureAwait(false);
            if (!reply.IsSuccessStatusCode)
                return NoResponse(Msg.Of("detail.httpStatus", ("status", (int)reply.StatusCode)));
            var body = await reply.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            response = IppMessage.Parse(body);
        }
        catch (HttpRequestException e) when (Net.FindSocketException(e) is { } se)
        {
            return CheckResult.Fail(FailureCode.PortBlocked, TcpPortCheck.PortBlocked(_host, _port, Net.Describe(se), rawPrintHint: false));
        }
        catch (HttpRequestException e)
        {
            return NoResponse(e.Message);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return NoResponse(new Msg("detail.timeout"));
        }
        catch (FormatException)
        {
            return NoResponse(new Msg("detail.invalidResponse"));
        }

        if (!response.IsSuccess)
            return NoResponse(Msg.Of("detail.ippStatus", ("status", response.StatusCode.ToString("X4", CultureInfo.InvariantCulture))));

        var (errors, warnings) = ClassifyReasons(response.StateReasons);
        if (response.PrinterState == 5 && errors.Count == 0)
            errors.Add(new Msg("reason.stopped"));
        if (errors.Count > 0)
        {
            return CheckResult
                .Fail(FailureCode.DeviceError, Msg.Of("msg.deviceError", ("reasons", new MsgList([.. errors]))))
                .WithNotes(warnings);
        }
        return CheckResult.Ok(warnings);
    }

    private static CheckResult NoResponse(object detail) =>
        CheckResult.Fail(FailureCode.NoResponse, Msg.Of("msg.noResponse.ipp", ("detail", detail)));

    /// <summary>
    /// Splits printer-state-reasons into errors and warnings. Per RFC 8011 a keyword
    /// with no suffix is an error; <c>-report</c> keywords are informational and ignored.
    /// </summary>
    public static (List<Msg> Errors, List<Msg> Warnings) ClassifyReasons(IEnumerable<string> reasons)
    {
        var errors = new List<Msg>();
        var warnings = new List<Msg>();
        foreach (var raw in reasons)
        {
            var reason = raw.Trim().ToLowerInvariant();
            if (reason is "" or "none" || reason.EndsWith("-report", StringComparison.Ordinal))
                continue;
            var isWarning = reason.EndsWith("-warning", StringComparison.Ordinal);
            var keyword = reason.EndsWith("-error", StringComparison.Ordinal) ? reason[..^"-error".Length]
                : isWarning ? reason[..^"-warning".Length]
                : reason;
            var text = Describe(keyword);
            if (isWarning)
                warnings.Add(text);
            else
                errors.Add(text);
        }
        return (errors, warnings);
    }

    private static Msg Describe(string keyword) => keyword switch
    {
        "media-empty" or "media-needed" => new Msg("reason.paperEnd"),
        "media-low" => new Msg("reason.paperNearEnd"),
        "media-jam" => new Msg("reason.paperJam"),
        "toner-empty" or "marker-supply-empty" => new Msg("reason.tonerEmpty"),
        "toner-low" or "marker-supply-low" => new Msg("reason.tonerLow"),
        "door-open" => new Msg("reason.doorOpen"),
        "cover-open" => new Msg("reason.coverOpen"),
        "paused" or "moving-to-paused" => new Msg("reason.paused"),
        "output-area-full" or "output-tray-missing" => new Msg("reason.outputFull"),
        "input-tray-missing" => new Msg("reason.trayMissing"),
        "offline" or "shutdown" => new Msg("reason.offline"),
        _ => Msg.Of("raw", ("text", keyword)),
    };

    private static SocketsHttpHandler CreateHandler(bool tls, TimeSpan timeout)
    {
        var handler = new SocketsHttpHandler { ConnectTimeout = timeout, PooledConnectionLifetime = TimeSpan.FromMinutes(5) };
        if (tls)
        {
            // Printers ship self-signed certificates; the IPP status is not sensitive.
            handler.SslOptions.RemoteCertificateValidationCallback = static (_, _, _, _) => true;
        }
        return handler;
    }

    public void Dispose() => _http.Dispose();
}
