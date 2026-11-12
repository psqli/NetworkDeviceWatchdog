using System.Drawing;
using System.Windows.Forms;
using NetworkDeviceWatchdog.App.Hosting;
using NetworkDeviceWatchdog.Configuration;
using NetworkDeviceWatchdog.Localization;
using NetworkDeviceWatchdog.Notifications;

namespace NetworkDeviceWatchdog.App.Windows;

/// <summary>Windows entry points for commands that need the UI.</summary>
internal static class WindowsApp
{
    private static bool s_uiInitialized;

    private static void EnsureUi()
    {
        if (s_uiInitialized)
            return;
        s_uiInitialized = true;
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
    }

    /// <summary>Runs the tray application until Exit, logoff or <c>--uninstall</c>.</summary>
    public static int Run(CommandLine cli, string configPath)
    {
        EnsureUi();
        using var instance = Startup.TryClaimInstance();
        if (instance is null)
        {
            MessageBox.Show(Localizer.Create(cli.Language)["app.alreadyRunning"], "Network Device Watchdog",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return Commands.Problems;
        }
        using var context = new TrayContext(cli, configPath);
        Application.Run(context);
        return Commands.Success;
    }

    /// <summary>Sends a test alert and keeps the UI alive until its window is closed.</summary>
    public static int RunTestAlert(CommandLine cli, string configPath)
    {
        EnsureUi();
        using var context = new TestAlertContext(cli, configPath);
        Application.Run(context);
        return Commands.Success;
    }

    public static void ShowStartupError(string message)
    {
        EnsureUi();
        MessageBox.Show(message, "Network Device Watchdog", MessageBoxButtons.OK, MessageBoxIcon.Error);
    }

    /// <summary>Shows the test alert, then exits once no window is open (or after 15 s).</summary>
    private sealed class TestAlertContext : ApplicationContext
    {
        private readonly Control _ui = new();
        private readonly NotifyIcon _icon = new() { Icon = SystemIcons.Application, Visible = true };
        private readonly WatchdogRuntime _runtime;
        private readonly System.Windows.Forms.Timer _timer = new() { Interval = 500 };
        private readonly DateTime _started = DateTime.UtcNow;

        public TestAlertContext(CommandLine cli, string configPath)
        {
            _ui.CreateControl();
            _runtime = WatchdogRuntime.Load(configPath, cli.Language, console: false, CreateNotifiers);
            _ = _runtime.Service.SendTestAlertAsync(CancellationToken.None);
            // Exit once the alert window has been closed; with only a tray notification,
            // stay long enough for it to be seen.
            var minimum = _runtime.Config.Notifications.AlertWindow ? TimeSpan.FromSeconds(2) : TimeSpan.FromSeconds(10);
            _timer.Tick += (_, _) =>
            {
                if (Application.OpenForms.Count == 0 && DateTime.UtcNow - _started > minimum)
                    ExitThread();
            };
            _timer.Start();
        }

        private IEnumerable<INotifier> CreateNotifiers(WatchdogConfig config, Localizer localizer)
        {
            if (config.Notifications.AlertWindow)
                yield return new AlertWindowNotifier(_ui, localizer, config.Notifications.AlertFontSize);
            if (config.Notifications.Desktop)
                yield return new TrayNotifier(_ui, _icon);
        }

        protected override void ExitThreadCore()
        {
            _timer.Dispose();
            _runtime.Dispose();
            _icon.Visible = false;
            _icon.Dispose();
            _ui.Dispose();
            base.ExitThreadCore();
        }
    }
}
