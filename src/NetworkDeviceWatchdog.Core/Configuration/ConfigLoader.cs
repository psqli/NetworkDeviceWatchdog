using System.Net.NetworkInformation;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NetworkDeviceWatchdog.Configuration;

/// <summary>Thrown when the configuration cannot be read or is invalid.</summary>
public sealed class ConfigException(string path, IReadOnlyList<string> errors)
    : Exception($"Invalid configuration in {path}:{Environment.NewLine}  - " + string.Join(Environment.NewLine + "  - ", errors))
{
    public string Path { get; } = path;

    public IReadOnlyList<string> Errors { get; } = errors;
}

/// <summary>Reads, validates and writes the JSON configuration.</summary>
public static class ConfigLoader
{
    /// <summary>Loads and validates a configuration file.</summary>
    /// <exception cref="ConfigException">The file is missing, malformed or invalid.</exception>
    public static WatchdogConfig Load(string path)
    {
        string json;
        try
        {
            json = File.ReadAllText(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new ConfigException(path, [e.Message]);
        }
        return Parse(json, path);
    }

    /// <summary>Parses and validates configuration JSON.</summary>
    public static WatchdogConfig Parse(string json, string sourceName = "configuration")
    {
        WatchdogConfig? config;
        try
        {
            config = JsonSerializer.Deserialize(json, ConfigJsonContext.Default.WatchdogConfig);
        }
        catch (JsonException e)
        {
            throw new ConfigException(sourceName, [DescribeJsonError(e)]);
        }
        if (config is null)
            throw new ConfigException(sourceName, ["The file is empty."]);

        var errors = Validate(config);
        if (errors.Count > 0)
            throw new ConfigException(sourceName, errors);
        return config;
    }

    /// <summary>Serializes a configuration (used by <c>--init</c> and tests).</summary>
    public static string Serialize(WatchdogConfig config) =>
        JsonSerializer.Serialize(config, ConfigJsonContext.Default.WatchdogConfig);

    /// <summary>Returns a list of problems; empty when the configuration is valid.</summary>
    public static IReadOnlyList<string> Validate(WatchdogConfig c)
    {
        var errors = new List<string>();
        if (c.IntervalSeconds < 5)
            errors.Add("intervalSeconds must be at least 5.");
        if (c.FailThreshold < 1)
            errors.Add("failThreshold must be at least 1.");
        if (c.BusyThreshold < 1)
            errors.Add("busyThreshold must be at least 1.");
        if (c.Notifications.AlertFontSize is < 8 or > 72)
            errors.Add("notifications.alertFontSize must be between 8 and 72.");
        if (!string.IsNullOrWhiteSpace(c.Notifications.NtfyUrl) && !IsHttpUrl(c.Notifications.NtfyUrl))
            errors.Add("notifications.ntfyUrl must be an http(s) URL.");
        if (c.Notifications.Command is { } cmd)
        {
            if (string.IsNullOrWhiteSpace(cmd.Path))
                errors.Add("notifications.command.path is required when notifications.command is set.");
            if (cmd.TimeoutSeconds < 1)
                errors.Add("notifications.command.timeoutSeconds must be at least 1.");
        }
        foreach (var (name, value) in new[]
                 {
                     ("pingMs", c.Timeouts.PingMs), ("connectMs", c.Timeouts.ConnectMs),
                     ("readMs", c.Timeouts.ReadMs), ("httpMs", c.Timeouts.HttpMs),
                 })
        {
            if (value is < 100 or > 120_000)
                errors.Add($"timeouts.{name} must be between 100 and 120000.");
        }

        if (c.Devices.Count == 0)
            errors.Add("devices is empty: add at least one device.");

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < c.Devices.Count; i++)
        {
            var d = c.Devices[i];
            var at = string.IsNullOrWhiteSpace(d.Name) ? $"devices[{i}]" : $"devices[{i}] \"{d.Name}\"";

            if (string.IsNullOrWhiteSpace(d.Name))
                errors.Add($"{at}: name is required.");
            else if (!names.Add(d.Name.Trim()))
                errors.Add($"{at}: the name is used by another device.");

            if (string.IsNullOrWhiteSpace(d.Host))
                errors.Add($"{at}: host is required.");
            else if (Uri.CheckHostName(d.Host.Trim()) == UriHostNameType.Unknown)
                errors.Add($"{at}: host \"{d.Host}\" is not a valid IP address or host name.");

            if (!DeviceKinds.TryParse(d.Type, out var kind))
            {
                errors.Add($"{at}: type \"{d.Type}\" is unknown; use one of: {string.Join(", ", DeviceKinds.Names)}.");
                continue;
            }

            if (d.Port is { } port && !IsPort(port))
                errors.Add($"{at}: port {port} is out of range (1-65535).");
            foreach (var p in d.TcpPorts ?? [])
            {
                if (!IsPort(p))
                    errors.Add($"{at}: tcpPorts contains {p}, which is out of range (1-65535).");
            }

            if (!string.IsNullOrWhiteSpace(d.Mac) && !TryParseMac(d.Mac, out _))
                errors.Add($"{at}: mac \"{d.Mac}\" is not a valid MAC address (use 00-11-22-33-44-55).");

            if (d.Http is { } http && !IsHttpUrl(http.Url))
                errors.Add($"{at}: http.url must be an absolute http(s) URL.");
            if (d.Http?.ExpectStatus is { } st && st is < 100 or > 599)
                errors.Add($"{at}: http.expectStatus must be an HTTP status code (100-599).");

            if (d.Ipp is { } ipp && kind == DeviceKind.Printer && ipp.Enabled && !ipp.Path.StartsWith('/'))
                errors.Add($"{at}: ipp.path must start with '/'.");

            if (kind == DeviceKind.Generic && !d.Ping && (d.TcpPorts is null || d.TcpPorts.Count == 0) && d.Http is null && d.Port is null)
                errors.Add($"{at}: a generic device needs at least one check: ping, port, tcpPorts or http.");
        }
        return errors;
    }

    /// <summary>Parses MAC addresses written as 00-11-22-33-44-55, 00:11:22:33:44:55 or 001122334455.</summary>
    public static bool TryParseMac(string text, out PhysicalAddress address)
    {
        var hex = new string(text.Where(Uri.IsHexDigit).ToArray());
        var separatorsOk = text.All(ch => Uri.IsHexDigit(ch) || ch is '-' or ':' or '.' or ' ');
        if (hex.Length == 12 && separatorsOk)
        {
            address = PhysicalAddress.Parse(hex.ToUpperInvariant());
            return true;
        }
        address = PhysicalAddress.None;
        return false;
    }

    private static bool IsPort(int port) => port is > 0 and <= 65535;

    private static bool IsHttpUrl(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps);

    private static string DescribeJsonError(JsonException e)
    {
        var where = e.Path is null ? "" : $" at {e.Path}";
        var line = e.LineNumber is { } l ? $" (line {l + 1})" : "";
        // System.Text.Json messages for unknown properties are long; keep the first sentence.
        var message = e.Message.Split(". Path:", 2)[0];
        return $"JSON error{where}{line}: {message}";
    }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true,
    PropertyNameCaseInsensitive = true,
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(WatchdogConfig))]
internal sealed partial class ConfigJsonContext : JsonSerializerContext;
