using System.Diagnostics;
using System.Globalization;
using System.Text;
using NetworkDeviceWatchdog.Checks;
using NetworkDeviceWatchdog.Configuration;
using NetworkDeviceWatchdog.Localization;

namespace NetworkDeviceWatchdog.Notifications;

public enum NotificationKind
{
    Problem,
    Recovery,
    Test,
}

/// <summary>An already-localized notification.</summary>
public sealed record Notification(
    NotificationKind Kind,
    string Device,
    string Host,
    FailureCode? Failure,
    string Title,
    string Body,
    DateTimeOffset Time);

/// <summary>Delivers notifications (desktop window, phone push, external command...).</summary>
public interface INotifier
{
    /// <summary>Name used in error messages.</summary>
    string Name { get; }

    Task NotifyAsync(Notification notification, CancellationToken cancellationToken);
}

/// <summary>Receives log lines.</summary>
public interface ILogSink
{
    void Write(DateTimeOffset time, string message);
}

/// <summary>Writes to the console.</summary>
public sealed class ConsoleLog : ILogSink
{
    public void Write(DateTimeOffset time, string message) =>
        Console.WriteLine($"{time.LocalDateTime:yyyy-MM-dd HH:mm:ss}  {message}");
}

/// <summary>Appends to one UTF-8 log file per month in a directory.</summary>
public sealed class FileLog(string directory) : ILogSink
{
    private readonly Lock _lock = new();

    public string Directory { get; } = directory;

    public void Write(DateTimeOffset time, string message)
    {
        var local = time.LocalDateTime;
        var file = Path.Combine(Directory, $"watchdog-{local:yyyy-MM}.log");
        var line = $"{local:yyyy-MM-dd HH:mm:ss}  {message}{Environment.NewLine}";
        lock (_lock)
        {
            try
            {
                System.IO.Directory.CreateDirectory(Directory);
                File.AppendAllText(file, line, Encoding.UTF8);
            }
            catch (IOException)
            {
                // Logging must never stop the watchdog.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}

/// <summary>Writes to several sinks.</summary>
public sealed class CompositeLog(params IReadOnlyList<ILogSink> sinks) : ILogSink
{
    public void Write(DateTimeOffset time, string message)
    {
        foreach (var sink in sinks)
            sink.Write(time, message);
    }
}

/// <summary>
/// Phone push via <a href="https://ntfy.sh">ntfy</a>: install the app, subscribe to a
/// topic, and set its URL. Titles are RFC 2047-encoded so accents and non-Latin
/// scripts survive HTTP headers.
/// </summary>
public sealed class NtfyNotifier(HttpClient http, Uri topicUrl, string? token) : INotifier
{
    public string Name => "ntfy";

    public async Task NotifyAsync(Notification n, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, topicUrl)
        {
            Content = new StringContent(n.Body, Encoding.UTF8, "text/plain"),
        };
        request.Headers.TryAddWithoutValidation("Title", TextEncoding.ToHeaderValue(n.Title));
        request.Headers.TryAddWithoutValidation("Priority", n.Kind == NotificationKind.Recovery ? "default" : "high");
        request.Headers.TryAddWithoutValidation("Tags", n.Kind == NotificationKind.Recovery ? "white_check_mark" : "warning");
        if (!string.IsNullOrEmpty(token))
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }
}

/// <summary>
/// Runs an external program per notification, for integrations the app does not
/// ship (e-mail, SMS gateways, MQTT, home automation...).
/// </summary>
public sealed class CommandNotifier(CommandNotificationConfig config) : INotifier
{
    public string Name => "command";

    public async Task NotifyAsync(Notification n, CancellationToken cancellationToken)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["kind"] = n.Kind.ToString().ToLowerInvariant(),
            ["device"] = n.Device,
            ["host"] = n.Host,
            ["failure"] = n.Failure?.ToString() ?? "",
            ["title"] = n.Title,
            ["body"] = n.Body,
            ["time"] = n.Time.ToString("O", CultureInfo.InvariantCulture),
        };
        var start = new ProcessStartInfo(config.Path)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in config.Arguments)
        {
            var expanded = arg;
            foreach (var (k, v) in values)
                expanded = expanded.Replace("{" + k + "}", v, StringComparison.Ordinal);
            start.ArgumentList.Add(expanded);
        }
        foreach (var (k, v) in values)
            start.Environment["NDW_" + k.ToUpperInvariant()] = v;

        using var process = Process.Start(start) ?? throw new InvalidOperationException($"Could not start {config.Path}.");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(config.TimeoutSeconds));
        try
        {
            await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            throw new TimeoutException($"{config.Path} did not finish within {config.TimeoutSeconds} s.");
        }
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"{config.Path} exited with code {process.ExitCode}.");
    }
}
