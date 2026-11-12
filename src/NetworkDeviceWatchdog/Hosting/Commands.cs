using System.Reflection;
using System.Runtime.InteropServices;
using NetworkDeviceWatchdog.Checks;
using NetworkDeviceWatchdog.Configuration;
using NetworkDeviceWatchdog.Localization;
using NetworkDeviceWatchdog.Monitoring;

namespace NetworkDeviceWatchdog.App.Hosting;

/// <summary>Commands that work the same on every platform.</summary>
internal static class Commands
{
    public const int Success = 0;
    public const int Problems = 1;
    public const int UsageError = 2;

    /// <summary>Localizer usable before a configuration is loaded.</summary>
    public static Localizer EarlyLocalizer(string? language) => Localizer.Create(language);

    public static string Version =>
        typeof(Commands).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? "unknown";

    public static int Init(CommandLine cli, TextWriter output)
    {
        var path = AppPaths.ResolveConfig(cli.ConfigPath);
        var l = EarlyLocalizer(cli.Language);
        if (File.Exists(path) && !cli.Force)
        {
            output.WriteLine($"{path} already exists. Use --force to overwrite it.");
            return UsageError;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, AppPaths.ReadSampleConfig());
        output.WriteLine(l.Format("app.configCreated", ("path", path)));
        return Success;
    }

    public static int Validate(CommandLine cli, TextWriter output)
    {
        var path = AppPaths.ResolveConfig(cli.ConfigPath);
        var l = EarlyLocalizer(cli.Language);
        try
        {
            var config = ConfigLoader.Load(path);
            l = Localizer.Create(cli.Language ?? config.Language, AppPaths.LocalesDirectory(config, path));
            output.WriteLine(l.Format("app.configValid", ("count", config.Devices.Count)));
            return Success;
        }
        catch (ConfigException e)
        {
            WriteConfigErrors(output, l, e);
            return Problems;
        }
    }

    /// <summary>Checks every device once and prints a table. Exit code 1 if any device has a problem.</summary>
    public static async Task<int> OnceAsync(WatchdogRuntime runtime, TextWriter output, CancellationToken cancellationToken)
    {
        var l = runtime.Localizer;
        var snapshots = await runtime.Service.CheckAllAsync(cancellationToken).ConfigureAwait(false);
        var rows = snapshots.Select(s => (
            Device: s.Device,
            Status: s.Health switch
            {
                DeviceHealth.Ok => l["status.ok"],
                DeviceHealth.Busy => l["status.busy"],
                DeviceHealth.Problem => l.Format(s.Failure!.Value.Title()),
                _ => l["status.unknown"],
            },
            Message: string.Join(" ", new[]
            {
                s.Message is null ? "" : l.Format(s.Message),
                s.Notes.Count == 0 ? "" : $"({string.Join(l["list.separator"], s.Notes.Select(l.Format))})",
            }.Where(t => t.Length > 0)))).ToList();

        var w1 = Math.Max(rows.Count == 0 ? 0 : rows.Max(r => r.Device.Length), 6);
        var w2 = Math.Max(rows.Count == 0 ? 0 : rows.Max(r => r.Status.Length), 6);
        foreach (var r in rows)
            output.WriteLine($"{r.Device.PadRight(w1)}  {r.Status.PadRight(w2)}  {r.Message}".TrimEnd());
        return snapshots.Any(s => s.Health == DeviceHealth.Problem) ? Problems : Success;
    }

    public static int Install(CommandLine cli, TextWriter output)
    {
        var path = AppPaths.ResolveConfig(cli.ConfigPath);
        var l = EarlyLocalizer(cli.Language);
        if (!File.Exists(path))
        {
            output.WriteLine(l.Format("app.configNotFound", ("path", path)));
            return Problems;
        }
        var target = Startup.Install(path);
        output.WriteLine(OperatingSystem.IsWindows()
            ? l.Format("app.installed", ("path", path))
            : l.Format("app.installLinux", ("path", target)));
        return Success;
    }

    public static int Uninstall(CommandLine cli, TextWriter output)
    {
        Startup.Uninstall();
        output.WriteLine(EarlyLocalizer(cli.Language)["app.uninstalled"]);
        return Success;
    }

    public static void WriteConfigErrors(TextWriter output, Localizer l, ConfigException e)
    {
        output.WriteLine(l.Format("app.configInvalid", ("path", e.Path)));
        foreach (var error in e.Errors)
            output.WriteLine("  - " + error);
    }

    /// <summary>
    /// Runs in the foreground until Ctrl+C or SIGTERM, reloading the configuration
    /// when the file changes (used on Linux/macOS, e.g. under systemd).
    /// </summary>
    public static async Task<int> RunConsoleAsync(CommandLine cli, string configPath)
    {
        using var instance = Startup.TryClaimInstance();
        if (instance is null)
        {
            Console.Error.WriteLine(EarlyLocalizer(cli.Language)["app.alreadyRunning"]);
            return Problems;
        }

        using var stop = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            stop.Cancel();
        };
        using var sigterm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, ctx =>
        {
            ctx.Cancel = true;
            stop.Cancel();
        });

        var runtime = WatchdogRuntime.Load(configPath, cli.Language, console: true);
        var reload = new SemaphoreSlim(0, 1);
        using var watcher = new ConfigWatcher(configPath);
        watcher.Changed += () =>
        {
            if (reload.CurrentCount == 0)
                reload.Release();
        };

        try
        {
            while (!stop.IsCancellationRequested)
            {
                using var round = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
                var running = runtime.Service.RunAsync(round.Token);
                var reloadRequested = reload.WaitAsync(stop.Token);
                await Task.WhenAny(running, reloadRequested).ConfigureAwait(false);
                if (!reloadRequested.IsCompletedSuccessfully)
                {
                    await running.ConfigureAwait(false);
                    break;
                }

                try
                {
                    var next = WatchdogRuntime.Load(configPath, cli.Language, console: true);
                    await round.CancelAsync().ConfigureAwait(false);
                    await running.ConfigureAwait(false);
                    runtime.Dispose();
                    runtime = next;
                    runtime.Log.Write(DateTimeOffset.UtcNow, runtime.Localizer.Format("app.configReloaded", ("path", configPath)));
                }
                catch (ConfigException e)
                {
                    // Keep monitoring with the previous configuration.
                    runtime.Log.Write(DateTimeOffset.UtcNow, e.Message);
                    await round.CancelAsync().ConfigureAwait(false);
                    await running.ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
        }
        finally
        {
            runtime.Dispose();
        }
        return Success;
    }
}
