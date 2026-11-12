namespace NetworkDeviceWatchdog.Configuration;

/// <summary>Root of the JSON configuration file.</summary>
public sealed class WatchdogConfig
{
    /// <summary><c>auto</c> (operating system language) or a tag such as <c>pt-BR</c>, <c>es</c>, <c>hi</c>, <c>zh</c>, <c>ar</c>.</summary>
    public string Language { get; set; } = "auto";

    /// <summary>Seconds between check rounds.</summary>
    public int IntervalSeconds { get; set; } = 60;

    /// <summary>Failed checks in a row before an alert is raised.</summary>
    public int FailThreshold { get; set; } = 3;

    /// <summary>
    /// Checks in a row during which this computer keeps a raw print connection open
    /// before the job is reported as stuck (ESC/POS printers only).
    /// </summary>
    public int BusyThreshold { get; set; } = 10;

    /// <summary>Log folder. Default: a <c>logs</c> folder next to the configuration file.</summary>
    public string? LogDirectory { get; set; }

    /// <summary>Folder with extra or overriding locale files. Default: <c>locales</c> next to the configuration file.</summary>
    public string? LocalesDirectory { get; set; }

    public NotificationConfig Notifications { get; set; } = new();

    public TimeoutConfig Timeouts { get; set; } = new();

    public List<DeviceConfig> Devices { get; set; } = [];
}

/// <summary>How alerts are delivered.</summary>
public sealed class NotificationConfig
{
    /// <summary>Windows: tray balloon / notification for problems and recoveries.</summary>
    public bool Desktop { get; set; } = true;

    /// <summary>Windows: always-on-top alert window that stays until someone presses OK.</summary>
    public bool AlertWindow { get; set; } = true;

    /// <summary>Body text size of the alert window, in points (title is 1.4×).</summary>
    public int AlertFontSize { get; set; } = 18;

    /// <summary>Optional ntfy topic URL for phone push notifications, e.g. <c>https://ntfy.sh/my-topic</c>.</summary>
    public string? NtfyUrl { get; set; }

    /// <summary>Optional ntfy access token (for protected topics or self-hosted servers).</summary>
    public string? NtfyToken { get; set; }

    /// <summary>Optional external program to run for every alert and recovery.</summary>
    public CommandNotificationConfig? Command { get; set; }

    /// <summary>Also notify when a device recovers.</summary>
    public bool NotifyRecovery { get; set; } = true;
}

/// <summary>
/// Runs a program for each notification. Arguments may contain the placeholders
/// <c>{kind}</c>, <c>{device}</c>, <c>{host}</c>, <c>{failure}</c>, <c>{title}</c>,
/// <c>{body}</c> and <c>{time}</c>; the same values are also passed as environment
/// variables <c>NDW_KIND</c>, <c>NDW_DEVICE</c>, ...
/// </summary>
public sealed class CommandNotificationConfig
{
    public string Path { get; set; } = "";

    public List<string> Arguments { get; set; } = [];

    /// <summary>The process is killed if it runs longer than this.</summary>
    public int TimeoutSeconds { get; set; } = 30;
}

/// <summary>Timeouts in milliseconds.</summary>
public sealed class TimeoutConfig
{
    public int PingMs { get; set; } = 1000;

    public int ConnectMs { get; set; } = 3000;

    public int ReadMs { get; set; } = 2000;

    public int HttpMs { get; set; } = 5000;
}

/// <summary>One monitored device.</summary>
public sealed class DeviceConfig
{
    /// <summary>Name shown in alerts. Must be unique.</summary>
    public string Name { get; set; } = "";

    /// <summary><c>escpos-printer</c>, <c>printer</c> or <c>generic</c>.</summary>
    public string Type { get; set; } = "generic";

    /// <summary>IP address or host name.</summary>
    public string Host { get; set; } = "";

    /// <summary>
    /// Main port. Defaults: 9100 for <c>escpos-printer</c>, 631 for <c>printer</c> (IPP).
    /// </summary>
    public int? Port { get; set; }

    /// <summary>Expected MAC address; enables IP-conflict detection (same subnet only).</summary>
    public string? Mac { get; set; }

    /// <summary>Check ICMP ping. Disable for devices that block ping.</summary>
    public bool Ping { get; set; } = true;

    /// <summary>Windows print queue to watch for jobs in error (Windows only).</summary>
    public string? WindowsQueue { get; set; }

    /// <summary>Extra TCP ports that must accept connections.</summary>
    public List<int>? TcpPorts { get; set; }

    /// <summary>Optional HTTP(S) health check.</summary>
    public HttpCheckConfig? Http { get; set; }

    /// <summary>IPP options for <c>printer</c> devices.</summary>
    public IppConfig? Ipp { get; set; }

    /// <summary>Set to false to keep a device in the file without monitoring it.</summary>
    public bool Enabled { get; set; } = true;
}

/// <summary>HTTP(S) health check.</summary>
public sealed class HttpCheckConfig
{
    public string Url { get; set; } = "";

    /// <summary>Expected status code. Default: any 2xx.</summary>
    public int? ExpectStatus { get; set; }

    /// <summary>Text the response body must contain.</summary>
    public string? ExpectBodyContains { get; set; }

    /// <summary>Accept self-signed or invalid TLS certificates (common on embedded devices).</summary>
    public bool IgnoreCertificateErrors { get; set; }
}

/// <summary>IPP (Internet Printing Protocol) status check for office printers.</summary>
public sealed class IppConfig
{
    /// <summary>Set to false for printers without IPP; only the TCP port is then checked.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Printer path. <c>/ipp/print</c> works for most IPP Everywhere / AirPrint printers.</summary>
    public string Path { get; set; } = "/ipp/print";

    /// <summary>Use IPPS (TLS). Printer certificates are usually self-signed and are not validated.</summary>
    public bool Tls { get; set; }
}
