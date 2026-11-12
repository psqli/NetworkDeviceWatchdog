using NetworkDeviceWatchdog.Checks;
using NetworkDeviceWatchdog.Configuration;
using NetworkDeviceWatchdog.Localization;
using NetworkDeviceWatchdog.Monitoring;
using NetworkDeviceWatchdog.Notifications;
using NetworkDeviceWatchdog.Platform;

namespace NetworkDeviceWatchdog.App.Hosting;

/// <summary>Where configuration and logs live.</summary>
internal static class AppPaths
{
    public const string ConfigFileName = "watchdog.json";

    /// <summary>Per-user application folder.</summary>
    public static string UserDirectory
    {
        get
        {
            if (OperatingSystem.IsWindows())
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NetworkDeviceWatchdog");
            var xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
            var baseDir = string.IsNullOrEmpty(xdg)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config")
                : xdg;
            return Path.Combine(baseDir, "network-device-watchdog");
        }
    }

    /// <summary>
    /// Resolves the configuration file: the explicit path, else <c>watchdog.json</c>
    /// next to the executable (portable install), else the user folder.
    /// </summary>
    public static string ResolveConfig(string? explicitPath)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath))
            return Path.GetFullPath(explicitPath);
        var beside = Path.Combine(AppContext.BaseDirectory, ConfigFileName);
        return File.Exists(beside) ? beside : Path.Combine(UserDirectory, ConfigFileName);
    }

    public static string LogDirectory(WatchdogConfig config, string configPath) =>
        ResolveRelative(config.LogDirectory, configPath, "logs");

    public static string LocalesDirectory(WatchdogConfig config, string configPath) =>
        ResolveRelative(config.LocalesDirectory, configPath, "locales");

    private static string ResolveRelative(string? value, string configPath, string defaultName)
    {
        var configDir = Path.GetDirectoryName(configPath) ?? ".";
        if (string.IsNullOrWhiteSpace(value))
            return Path.Combine(configDir, defaultName);
        var expanded = Environment.ExpandEnvironmentVariables(value);
        return Path.IsPathRooted(expanded) ? expanded : Path.GetFullPath(Path.Combine(configDir, expanded));
    }

    public static string ReadSampleConfig()
    {
        using var stream = typeof(AppPaths).Assembly.GetManifestResourceStream("watchdog.sample.json")
                           ?? throw new InvalidOperationException("Sample configuration resource missing.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}

/// <summary>Builds platform-specific notifiers (alert window, tray notification...).</summary>
internal delegate IEnumerable<INotifier> NotifierFactory(WatchdogConfig config, Localizer localizer);

/// <summary>Everything built from one configuration file: localizer, log, service.</summary>
internal sealed class WatchdogRuntime : IDisposable
{
    private static readonly HttpClient SharedHttp = new() { Timeout = TimeSpan.FromSeconds(15) };

    private WatchdogRuntime(string configPath, WatchdogConfig config, Localizer localizer, ILogSink log, string logDirectory, WatchdogService service)
    {
        ConfigPath = configPath;
        Config = config;
        Localizer = localizer;
        Log = log;
        LogDirectory = logDirectory;
        Service = service;
    }

    public string ConfigPath { get; }

    public WatchdogConfig Config { get; }

    public Localizer Localizer { get; }

    public ILogSink Log { get; }

    public string LogDirectory { get; }

    public WatchdogService Service { get; }

    /// <summary>Loads the configuration and builds the service.</summary>
    /// <exception cref="ConfigException">The configuration is missing or invalid.</exception>
    public static WatchdogRuntime Load(string configPath, string? languageOverride, bool console, NotifierFactory? platformNotifiers = null)
    {
        if (!File.Exists(configPath))
            throw new FileNotFoundException(configPath, configPath);
        var config = ConfigLoader.Load(configPath);
        var localizer = Localizer.Create(languageOverride ?? config.Language, AppPaths.LocalesDirectory(config, configPath));
        var logDir = AppPaths.LogDirectory(config, configPath);
        ILogSink log = console ? new CompositeLog(new FileLog(logDir), new ConsoleLog()) : new FileLog(logDir);

        var notifiers = new List<INotifier>();
        if (platformNotifiers is not null)
            notifiers.AddRange(platformNotifiers(config, localizer));
        if (!string.IsNullOrWhiteSpace(config.Notifications.NtfyUrl))
            notifiers.Add(new NtfyNotifier(SharedHttp, new Uri(config.Notifications.NtfyUrl), config.Notifications.NtfyToken));
        if (config.Notifications.Command is { } command)
            notifiers.Add(new CommandNotifier(command));

        var services = new ProbeServices
        {
            PrintQueues = OperatingSystem.IsWindows() ? new WindowsPrintQueueInspector() : null,
        };
        var service = WatchdogService.Create(config, services, notifiers, localizer, log);
        return new WatchdogRuntime(configPath, config, localizer, log, logDir, service);
    }

    public void Dispose() => Service.Dispose();
}

/// <summary>Raises <see cref="Changed"/> (debounced) when the configuration file is saved.</summary>
internal sealed class ConfigWatcher : IDisposable
{
    private readonly FileSystemWatcher _watcher;
    private readonly Timer _debounce;

    public ConfigWatcher(string configPath)
    {
        _debounce = new Timer(_ => Changed?.Invoke(), null, Timeout.Infinite, Timeout.Infinite);
        _watcher = new FileSystemWatcher(Path.GetDirectoryName(configPath)!, Path.GetFileName(configPath))
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
        };
        // Editors save in bursts (truncate, write, rename): wait for the file to settle.
        FileSystemEventHandler handler = (_, _) => _debounce.Change(1500, Timeout.Infinite);
        _watcher.Changed += handler;
        _watcher.Created += handler;
        _watcher.Renamed += (_, _) => _debounce.Change(1500, Timeout.Infinite);
        _watcher.EnableRaisingEvents = true;
    }

    public event Action? Changed;

    public void Dispose()
    {
        _watcher.Dispose();
        _debounce.Dispose();
    }
}
