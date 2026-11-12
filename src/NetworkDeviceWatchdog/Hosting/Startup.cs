using System.Diagnostics;
using Microsoft.Win32;

namespace NetworkDeviceWatchdog.App.Hosting;

/// <summary>Start-at-logon registration and single-instance coordination.</summary>
internal static class Startup
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValue = "NetworkDeviceWatchdog";
    private const string LinuxUnitName = "network-device-watchdog.service";
    private const string InstanceName = "NetworkDeviceWatchdog.Instance";
    private const string ExitEventName = @"Local\NetworkDeviceWatchdog.Exit";

    private static string ExecutablePath =>
        Environment.ProcessPath ?? throw new InvalidOperationException("Cannot determine the program path.");

    /// <summary>Registers the watchdog to start at logon. Returns the file or key written.</summary>
    public static string Install(string configPath)
    {
        if (OperatingSystem.IsWindows())
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
            key.SetValue(RunValue, $"\"{ExecutablePath}\" --config \"{configPath}\"");
            // Start it now as well, so there is no need to log off.
            Process.Start(new ProcessStartInfo(ExecutablePath) { ArgumentList = { "--config", configPath }, UseShellExecute = false });
            return $@"HKEY_CURRENT_USER\{RunKey}\{RunValue}";
        }

        var unitDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "systemd", "user");
        Directory.CreateDirectory(unitDir);
        var unitPath = Path.Combine(unitDir, LinuxUnitName);
        File.WriteAllText(unitPath, $"""
            [Unit]
            Description=Network Device Watchdog
            After=network-online.target

            [Service]
            ExecStart="{ExecutablePath}" --config "{configPath}"
            Restart=always
            RestartSec=10

            [Install]
            WantedBy=default.target

            """);
        return unitPath;
    }

    /// <summary>Removes the registration and asks a running instance to exit.</summary>
    public static void Uninstall()
    {
        if (OperatingSystem.IsWindows())
        {
            using (var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true))
                key?.DeleteValue(RunValue, throwOnMissingValue: false);
            if (EventWaitHandle.TryOpenExisting(ExitEventName, out var exit))
            {
                using (exit)
                    exit.Set();
            }
            return;
        }
        var unitPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "systemd", "user", LinuxUnitName);
        if (File.Exists(unitPath))
            File.Delete(unitPath);
    }

    /// <summary>
    /// Claims the single-instance lock. Returns null if another instance runs.
    /// Dispose the result when exiting.
    /// </summary>
    public static IDisposable? TryClaimInstance()
    {
        var mutex = new Mutex(initiallyOwned: false, InstanceName);
        try
        {
            if (mutex.WaitOne(0))
                return new Releaser(mutex);
        }
        catch (AbandonedMutexException)
        {
            return new Releaser(mutex);   // previous instance crashed: take over
        }
        mutex.Dispose();
        return null;
    }

    /// <summary>Windows: an event that <c>--uninstall</c> sets to stop the running instance.</summary>
    public static EventWaitHandle? CreateExitEvent() =>
        OperatingSystem.IsWindows() ? new EventWaitHandle(false, EventResetMode.AutoReset, ExitEventName) : null;

    private sealed class Releaser(Mutex mutex) : IDisposable
    {
        public void Dispose()
        {
            mutex.ReleaseMutex();
            mutex.Dispose();
        }
    }
}
