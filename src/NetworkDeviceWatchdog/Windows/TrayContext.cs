using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;
using NetworkDeviceWatchdog.App.Hosting;
using NetworkDeviceWatchdog.Checks;
using NetworkDeviceWatchdog.Configuration;
using NetworkDeviceWatchdog.Localization;
using NetworkDeviceWatchdog.Monitoring;
using NetworkDeviceWatchdog.Notifications;

namespace NetworkDeviceWatchdog.App.Windows;

/// <summary>
/// The watchdog as a notification-area (tray) application: no console window, a
/// status menu, alert windows, and live reload when the configuration is saved.
/// </summary>
internal sealed class TrayContext : ApplicationContext
{
    private readonly CommandLine _cli;
    private readonly string _configPath;
    private readonly Control _ui;
    private readonly NotifyIcon _icon;
    private readonly ContextMenuStrip _menu;
    private readonly ConfigWatcher _watcher;
    private readonly EventWaitHandle? _exitEvent;
    private readonly RegisteredWaitHandle? _exitWait;
    private WatchdogRuntime _runtime;
    private CancellationTokenSource _stop = new();
    private Task _loop = Task.CompletedTask;
    private IReadOnlyList<DeviceSnapshot> _snapshots = [];

    public TrayContext(CommandLine cli, string configPath)
    {
        _cli = cli;
        _configPath = configPath;

        // A hidden control created on the UI thread, used to marshal work onto it.
        _ui = new Control();
        _ui.CreateControl();

        _menu = new ContextMenuStrip();
        _icon = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            ContextMenuStrip = _menu,
            Visible = true,
        };
        _icon.DoubleClick += (_, _) => CheckNow();

        _runtime = LoadRuntime();   // throws ConfigException on a bad file; Program shows it
        Start();

        _watcher = new ConfigWatcher(configPath);
        _watcher.Changed += () => _ui.BeginInvoke(Reload);

        // --uninstall signals this event to stop the running instance.
        _exitEvent = Startup.CreateExitEvent();
        if (_exitEvent is not null)
        {
            _exitWait = ThreadPool.RegisterWaitForSingleObject(_exitEvent,
                (_, _) => _ui.BeginInvoke(ExitThread), null, Timeout.Infinite, executeOnlyOnce: true);
        }
    }

    private Localizer L => _runtime.Localizer;

    private WatchdogRuntime LoadRuntime() =>
        WatchdogRuntime.Load(_configPath, _cli.Language, console: false, CreateNotifiers);

    private IEnumerable<INotifier> CreateNotifiers(WatchdogConfig config, Localizer localizer)
    {
        if (config.Notifications.AlertWindow)
            yield return new AlertWindowNotifier(_ui, localizer, config.Notifications.AlertFontSize);
        if (config.Notifications.Desktop)
            yield return new TrayNotifier(_ui, _icon);
    }

    private void Start()
    {
        _stop = new CancellationTokenSource();
        _runtime.Service.StatusChanged += OnStatusChanged;
        var service = _runtime.Service;
        var token = _stop.Token;
        _loop = Task.Run(() => service.RunAsync(token));
        _snapshots = service.Snapshots;
        RebuildMenu();
    }

    private void Stop()
    {
        _runtime.Service.StatusChanged -= OnStatusChanged;
        _stop.Cancel();
        try
        {
            _loop.Wait(TimeSpan.FromSeconds(10));
        }
        catch (AggregateException)
        {
            // The loop logs its own failures; shutting down regardless.
        }
        _stop.Dispose();
    }

    private void Reload()
    {
        WatchdogRuntime next;
        try
        {
            next = LoadRuntime();
        }
        catch (Exception e) when (e is ConfigException or FileNotFoundException or IOException)
        {
            var details = e is ConfigException ce ? string.Join(Environment.NewLine, ce.Errors.Take(3)) : e.Message;
            _runtime.Log.Write(DateTimeOffset.UtcNow, e.Message);
            _icon.ShowBalloonTip(10_000, TrayNotifier.Truncate(L.Format("app.configInvalid", ("path", Path.GetFileName(_configPath))), 63),
                TrayNotifier.Truncate(details, 255), ToolTipIcon.Warning);
            return;   // keep monitoring with the previous configuration
        }
        Stop();
        _runtime.Dispose();
        _runtime = next;
        Start();
        _runtime.Log.Write(DateTimeOffset.UtcNow, L.Format("app.configReloaded", ("path", _configPath)));
    }

    private void OnStatusChanged(IReadOnlyList<DeviceSnapshot> snapshots) =>
        _ui.BeginInvoke(() =>
        {
            _snapshots = snapshots;
            RebuildMenu();
        });

    private void CheckNow()
    {
        var service = _runtime.Service;
        var token = _stop.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                await service.CheckAllAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        });
    }

    private void RebuildMenu()
    {
        var problems = _snapshots.Count(s => s.Health == DeviceHealth.Problem);
        var allChecked = _snapshots.Count > 0 && _snapshots.All(s => s.Health != DeviceHealth.Unknown);
        _icon.Icon = problems > 0 ? SystemIcons.Warning : allChecked ? SystemIcons.Information : SystemIcons.Application;
        var summary = problems > 0 ? L.Format("tray.problems", ("count", problems)) : allChecked ? L["tray.allOk"] : L["app.name"];
        _icon.Text = TrayNotifier.Truncate($"{L["app.name"]}\n{summary}", 127);

        _menu.SuspendLayout();
        foreach (ToolStripItem old in _menu.Items.Cast<ToolStripItem>().ToArray())
            old.Dispose();
        _menu.Items.Clear();
        _menu.RightToLeft = L.IsRightToLeft ? RightToLeft.Yes : RightToLeft.No;

        _menu.Items.Add(new ToolStripMenuItem(summary) { Enabled = false });
        _menu.Items.Add(new ToolStripSeparator());
        foreach (var s in _snapshots)
        {
            var status = s.Health switch
            {
                DeviceHealth.Ok => L["status.ok"],
                DeviceHealth.Busy => L["status.busy"],
                DeviceHealth.Problem => L.Format(s.Failure!.Value.Title()),
                _ => L["status.unknown"],
            };
            var item = new ToolStripMenuItem(L.Format("tray.deviceLine", ("device", s.Device), ("status", status)));
            if (s.Message is not null)
                item.ToolTipText = L.Format(s.Message);
            if (s.Health == DeviceHealth.Problem)
                item.Font = new Font(item.Font, FontStyle.Bold);
            _menu.Items.Add(item);
        }
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(new ToolStripMenuItem(L["tray.checkNow"], null, (_, _) => CheckNow()));
        _menu.Items.Add(new ToolStripMenuItem(L["tray.editConfig"], null, (_, _) => Open("notepad.exe", _configPath)));
        _menu.Items.Add(new ToolStripMenuItem(L["tray.openLog"], null, (_, _) =>
        {
            Directory.CreateDirectory(_runtime.LogDirectory);
            Open("explorer.exe", _runtime.LogDirectory);
        }));
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(new ToolStripMenuItem(L["tray.exit"], null, (_, _) => ExitThread()));
        _menu.ResumeLayout();
    }

    private static void Open(string program, string argument)
    {
        try
        {
            Process.Start(new ProcessStartInfo(program) { ArgumentList = { argument }, UseShellExecute = true });
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // Nothing sensible to do if the editor or Explorer cannot be started.
        }
    }

    protected override void ExitThreadCore()
    {
        _exitWait?.Unregister(null);
        _exitEvent?.Dispose();
        _watcher.Dispose();
        Stop();
        _runtime.Dispose();
        _icon.Visible = false;
        _icon.Dispose();
        _menu.Dispose();
        _ui.Dispose();
        base.ExitThreadCore();
    }
}
