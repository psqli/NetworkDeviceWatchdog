using NetworkDeviceWatchdog.Localization;

namespace NetworkDeviceWatchdog.Checks;

/// <summary>
/// HTTP(S) health check for embedded devices and services. Fails with
/// <see cref="FailureCode.PortBlocked"/> when the connection fails,
/// <see cref="FailureCode.NoResponse"/> on timeout, and
/// <see cref="FailureCode.HealthCheckFailed"/> on an unexpected status or body.
/// </summary>
public sealed class HttpCheck : IDeviceCheck, IDisposable
{
    private const int MaxBodyBytes = 1 << 20;

    private readonly HttpClient _http;
    private readonly Uri _url;
    private readonly int? _expectStatus;
    private readonly string? _expectBody;

    public HttpCheck(Uri url, int? expectStatus, string? expectBodyContains, bool ignoreCertificateErrors, TimeSpan timeout,
        HttpMessageHandler? handler = null)
    {
        _url = url;
        _expectStatus = expectStatus;
        _expectBody = string.IsNullOrEmpty(expectBodyContains) ? null : expectBodyContains;
        if (handler is null)
        {
            var sockets = new SocketsHttpHandler { ConnectTimeout = timeout, PooledConnectionLifetime = TimeSpan.FromMinutes(5) };
            if (ignoreCertificateErrors)
                sockets.SslOptions.RemoteCertificateValidationCallback = static (_, _, _, _) => true;
            handler = sockets;
        }
        _http = new HttpClient(handler, disposeHandler: true) { Timeout = timeout };
    }

    public string Name => "http";

    public async Task<CheckResult> RunAsync(CancellationToken cancellationToken)
    {
        var url = _url.ToString();
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, _url);
            using var reply = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            var status = (int)reply.StatusCode;
            var statusOk = _expectStatus is { } expected ? status == expected : status is >= 200 and < 300;
            if (!statusOk)
            {
                return CheckResult.Fail(FailureCode.HealthCheckFailed, Msg.Of("msg.http.status",
                    ("url", url), ("status", status), ("expected", _expectStatus?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "2xx")));
            }
            if (_expectBody is not null)
            {
                var body = await ReadLimitedAsync(reply.Content, cancellationToken).ConfigureAwait(false);
                if (!body.Contains(_expectBody, StringComparison.Ordinal))
                    return CheckResult.Fail(FailureCode.HealthCheckFailed, Msg.Of("msg.http.body", ("url", url), ("text", _expectBody)));
            }
            return CheckResult.Ok();
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return CheckResult.Fail(FailureCode.NoResponse, Msg.Of("msg.noResponse.http", ("url", url), ("detail", new Msg("detail.timeout"))));
        }
        catch (HttpRequestException e)
        {
            object detail = Net.FindSocketException(e) is { } se ? Net.Describe(se) : e.Message;
            return CheckResult.Fail(FailureCode.PortBlocked, Msg.Of("msg.http.connect", ("url", url), ("detail", detail)));
        }
    }

    private static async Task<string> ReadLimitedAsync(HttpContent content, CancellationToken cancellationToken)
    {
        await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int n;
        while (buffer.Length < MaxBodyBytes &&
               (n = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            buffer.Write(chunk, 0, n);
        }
        return System.Text.Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }

    public void Dispose() => _http.Dispose();
}
