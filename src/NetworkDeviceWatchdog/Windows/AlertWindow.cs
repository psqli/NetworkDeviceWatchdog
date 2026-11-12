using System.Drawing;
using System.Media;
using System.Windows.Forms;
using NetworkDeviceWatchdog.Localization;
using NetworkDeviceWatchdog.Notifications;

namespace NetworkDeviceWatchdog.App.Windows;

/// <summary>
/// Large, always-on-top alert that stays until someone presses OK. It has no
/// close button, plays the system error sound, and mirrors its layout for
/// right-to-left languages.
/// </summary>
internal sealed class AlertWindow : Form
{
    private readonly List<Font> _fonts = [];

    public AlertWindow(Notification notification, Localizer localizer, int fontSize)
    {
        var size = (float)fontSize;
        var family = localizer.FontFamily;

        Text = notification.Title;
        TopMost = true;
        ControlBox = false;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterScreen;
        ShowInTaskbar = true;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        BackColor = Color.White;
        Padding = new Padding(Px(24));
        if (localizer.IsRightToLeft)
        {
            RightToLeft = RightToLeft.Yes;
            RightToLeftLayout = true;
        }

        var column = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            AutoSize = true,
            WrapContents = false,
        };

        var header = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight,
            AutoSize = true,
            WrapContents = false,
        };
        var iconSide = (int)(size * 3.2f);
        var icon = new PictureBox
        {
            Image = SystemIcons.Error.ToBitmap(),
            SizeMode = PictureBoxSizeMode.Zoom,
            Size = new Size(iconSide, iconSide),
        };
        var title = new Label
        {
            Text = notification.Title,
            Font = MakeFont(family, size * 1.4f, FontStyle.Bold),
            ForeColor = Color.FromArgb(192, 0, 0),
            AutoSize = true,
            MaximumSize = new Size(Px(900), 0),
            Margin = new Padding(Px(12), (int)(size * 0.8f), 0, 0),
        };
        header.Controls.Add(icon);
        header.Controls.Add(title);

        var body = new Label
        {
            Text = notification.Body,
            Font = MakeFont(family, size, FontStyle.Regular),
            AutoSize = true,
            MaximumSize = new Size(Px(1000), 0),
            Margin = new Padding(0, Px(16), 0, Px(8)),
        };

        var when = new Label
        {
            Text = notification.Time.LocalDateTime.ToString("G", System.Globalization.CultureInfo.CurrentCulture),
            Font = MakeFont(family, size * 0.7f, FontStyle.Regular),
            ForeColor = Color.Gray,
            AutoSize = true,
        };

        var ok = new Button
        {
            Text = localizer["alert.ok"],
            Font = MakeFont(family, size, FontStyle.Bold),
            AutoSize = true,
            MinimumSize = new Size((int)(size * 9), (int)(size * 3)),
            Margin = new Padding(0, Px(20), 0, 0),
        };
        ok.Click += (_, _) => Close();
        AcceptButton = ok;

        column.Controls.Add(header);
        column.Controls.Add(body);
        column.Controls.Add(when);
        column.Controls.Add(ok);
        Controls.Add(column);

        Shown += (_, _) =>
        {
            Activate();
            SystemSounds.Hand.Play();
        };
    }

    private int Px(int logical) => LogicalToDeviceUnits(logical);

    private Font MakeFont(string family, float points, FontStyle style)
    {
        var font = new Font(family, points, style);
        _fonts.Add(font);
        return font;
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            foreach (var font in _fonts)
                font.Dispose();
            _fonts.Clear();
        }
    }
}

/// <summary>Opens an <see cref="AlertWindow"/> for problems (not for recoveries).</summary>
internal sealed class AlertWindowNotifier(Control ui, Localizer localizer, int fontSize) : INotifier
{
    public string Name => "alert window";

    public Task NotifyAsync(Notification notification, CancellationToken cancellationToken)
    {
        if (notification.Kind == NotificationKind.Recovery)
            return Task.CompletedTask;
        ui.BeginInvoke(() =>
        {
            var window = new AlertWindow(notification, localizer, fontSize);
            window.FormClosed += (_, _) => window.Dispose();
            window.Show();
        });
        return Task.CompletedTask;
    }
}

/// <summary>Shows problems and recoveries as Windows notifications from the tray icon.</summary>
internal sealed class TrayNotifier(Control ui, NotifyIcon icon) : INotifier
{
    public string Name => "desktop notification";

    public Task NotifyAsync(Notification notification, CancellationToken cancellationToken)
    {
        var tipIcon = notification.Kind == NotificationKind.Recovery ? ToolTipIcon.Info : ToolTipIcon.Error;
        ui.BeginInvoke(() => icon.ShowBalloonTip(10_000, Truncate(notification.Title, 63), Truncate(notification.Body, 255), tipIcon));
        return Task.CompletedTask;
    }

    internal static string Truncate(string text, int max) => text.Length <= max ? text : text[..(max - 1)] + "…";
}
