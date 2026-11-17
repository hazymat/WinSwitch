using System.Drawing;
using System.Windows.Forms;
using WinSwitch.Power;
namespace WinSwitch.Shell;

// The whole everyday app: profiles, tweaks, Keep awake and Power, in the tray menu (SPEC.md › Tray). WinForms NotifyIcon runs fine on the WPF dispatcher
public sealed class TrayIcon : IDisposable
{
    readonly NotifyIcon notifyIcon;
    readonly ContextMenuStrip trayMenu = new();
    readonly Icon baseIcon = LoadIcon();
    string iconColour = "";
    App App => App.Instance;

    public TrayIcon()
    {
        trayMenu.Opening += (sender, args) => BuildMenu();
        notifyIcon = new NotifyIcon { Icon = baseIcon, Text = "WinSwitch", ContextMenuStrip = trayMenu, Visible = true };
        // Left-click opens the same menu (the profiles are the point)
        notifyIcon.MouseUp += (sender, args) => { if (args.Button == MouseButtons.Left) typeof(NotifyIcon).GetMethod("ShowContextMenu", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)?.Invoke(notifyIcon, null); };
        App.Engine.Changed += Refresh; App.Notices.Changed += Refresh; App.Updater.Changed += Refresh; PowerActions.AwakeChanged += Refresh;
        Refresh();
    }

    void BuildMenu()
    {
        trayMenu.Items.Clear();
        var engine = App.Engine; var settings = App.Settings;
        // The version, just the number, in a small shaded row at the top
        trayMenu.Items.Add(new ToolStripMenuItem(App.Updater.CurrentVersion) { Enabled = false, BackColor = Color.FromArgb(236, 236, 236), Font = new Font(SystemFonts.MenuFont ?? Control.DefaultFont, FontStyle.Regular) });
        if (App.Updater.ReadyVersion != "") trayMenu.Items.Add(Bold($"Restart to update to {App.Updater.ReadyVersion}", async () => await App.Updater.RestartToUpdateAsync()));
        if (App.Notices.Unanswered.Count > 0) trayMenu.Items.Add(Bold(App.Notices.Unanswered.Count == 1 ? "1 question waiting" : $"{App.Notices.Unanswered.Count} questions waiting", App.Notices.ShowUnanswered));
        if (settings.Power.Paused)
        {
            trayMenu.Items.Add(new ToolStripMenuItem("WinSwitch is paused: Windows' own settings are in charge") { Enabled = false });
            trayMenu.Items.Add(Bold("Resume", engine.Resume));
        }
        else
        {
            trayMenu.Items.Add(new ToolStripMenuItem("Automatic", null, (sender, args) => engine.GoAutomatic()) { Checked = engine.Automatic });
            if (engine.Reason != "") trayMenu.Items.Add(new ToolStripMenuItem("      " + engine.Reason) { Enabled = false });
        }
        trayMenu.Items.Add(new ToolStripSeparator());
        foreach (var profile in settings.Profiles)
        {
            var item = new ToolStripMenuItem(profile.Name, Dot(profile.Colour), (sender, args) => engine.HoldProfile(profile.Id)) { Checked = !settings.Power.Paused && engine.ActiveProfile?.Id == profile.Id };
            if (item.Checked) item.Font = new Font(item.Font, FontStyle.Bold);
            trayMenu.Items.Add(item);
        }
        if (settings.Tweaks.Count > 0)
        {
            trayMenu.Items.Add(new ToolStripSeparator());
            foreach (var tweak in settings.Tweaks) { var on = engine.TweakIsOn(tweak); trayMenu.Items.Add(new ToolStripMenuItem(tweak.Name + (tweak.Scheduled ? $"  ({tweak.From} to {tweak.To})" : ""), null, (sender, args) => engine.SetTweak(tweak, !on)) { Checked = on }); }
        }
        // Remote control, its own group: what's on, and a way into its settings
        trayMenu.Items.Add(new ToolStripSeparator());
        var remote = App.Settings.Remote;
        var remoteParts = new List<string>(); if (remote.PhoneOn) remoteParts.Add(App.Phone.Running ? "phone page on" : "phone page has a problem"); if (remote.MqttOn) remoteParts.Add(App.Mqtt.Status.StartsWith("Connected") ? "MQTT connected" : "MQTT not connected");
        trayMenu.Items.Add(new ToolStripMenuItem("Remote control: " + (remoteParts.Count == 0 ? "off" : string.Join(", ", remoteParts)), null, (sender, args) => App.OpenSettings("Phone")));
        var awake = new ToolStripMenuItem(PowerActions.Awake ? AwakeLabel() : "Keep awake");
        if (PowerActions.Awake) awake.DropDownItems.Add("Stop keeping awake", null, (sender, args) => PowerActions.StopKeepingAwake());
        foreach (var (label, minutes) in new[] { ("30 minutes", 30), ("1 hour", 60), ("2 hours", 120), ("Until I turn it off", 0) }) awake.DropDownItems.Add(label, null, (sender, args) => PowerActions.KeepAwake(minutes == 0 ? null : TimeSpan.FromMinutes(minutes)));
        awake.Checked = PowerActions.Awake;
        trayMenu.Items.Add(awake);
        var power = new ToolStripMenuItem("Power");
        power.DropDownItems.Add("Screen off now", null, (sender, args) => PowerActions.ScreenOff());
        power.DropDownItems.Add("Lock", null, (sender, args) => PowerActions.Lock());
        power.DropDownItems.Add("Sleep", null, (sender, args) => PowerActions.Sleep());
        if (PowerApi.CanHibernate()) power.DropDownItems.Add("Hibernate", null, (sender, args) => PowerActions.Hibernate());
        power.DropDownItems.Add(new ToolStripSeparator());
        power.DropDownItems.Add("Restart…", null, (sender, args) => { if (Confirm("Restart this PC now?")) PowerActions.Restart(); });
        power.DropDownItems.Add("Shut down…", null, (sender, args) => { if (Confirm("Shut down this PC now?")) PowerActions.ShutDown(); });
        trayMenu.Items.Add(power);
        trayMenu.Items.Add(new ToolStripSeparator());
        trayMenu.Items.Add("Settings", null, (sender, args) => App.OpenSettings());
        trayMenu.Items.Add("Quit", null, (sender, args) => App.Quit());
    }

    static ToolStripMenuItem Bold(string text, Action action) { var item = new ToolStripMenuItem(text, null, (sender, args) => action()); item.Font = new Font(item.Font, FontStyle.Bold); return item; }
    static string AwakeLabel() => PowerActions.AwakeUntil == DateTime.MaxValue ? "Keeping awake" : $"Keeping awake until {PowerActions.AwakeUntil:HH:mm}";
    static bool Confirm(string question) => System.Windows.MessageBox.Show(question, "WinSwitch", System.Windows.MessageBoxButton.OKCancel, System.Windows.MessageBoxImage.Question) == System.Windows.MessageBoxResult.OK;

    // Hover text and the profile's colour dot on the icon
    void Refresh() => App.Dispatcher.BeginInvoke(() =>
    {
        var engine = App.Engine; var (pluggedIn, percent, hasBattery) = PowerApi.PowerStatus();
        var state = App.Settings.Power.Paused ? "paused" : engine.ActiveProfile?.Name ?? "starting";
        var power = hasBattery ? $" ({(pluggedIn ? "plugged in" : "on battery")}, {percent}%)" : "";
        var text = $"WinSwitch: {state}{power}{(PowerActions.Awake ? ", awake" : "")}";
        notifyIcon.Text = text.Length > 63 ? text[..63] : text;
        var colour = App.Settings.Power.Paused ? "" : engine.ActiveProfile?.Colour ?? "";
        if (App.Notices.Unanswered.Count > 0) colour += "?";
        if (colour != iconColour) { iconColour = colour; var previous = notifyIcon.Icon; notifyIcon.Icon = Badged(colour.TrimEnd('?'), colour.EndsWith('?')); if (previous != baseIcon) previous?.Dispose(); }
    });

    Icon Badged(string colour, bool question)
    {
        if (colour == "" && !question) return baseIcon;
        var size = SystemInformation.SmallIconSize.Width;
        using var bitmap = new Bitmap(size, size);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            graphics.DrawIcon(baseIcon, new Rectangle(0, 0, size, size));
            var dot = size * 7 / 16;
            if (colour != "") { using var brush = new SolidBrush(ColorTranslator.FromHtml(colour)); using var ring = new Pen(Color.FromArgb(20, 22, 28), Math.Max(1, size / 16)); graphics.FillEllipse(brush, size - dot, size - dot, dot - 1, dot - 1); graphics.DrawEllipse(ring, size - dot, size - dot, dot - 1, dot - 1); }
            if (question) { using var amber = new SolidBrush(Color.FromArgb(224, 145, 47)); graphics.FillEllipse(amber, size - dot, 0, dot - 1, dot - 1); }
        }
        return Icon.FromHandle(bitmap.GetHicon());
    }

    static Image Dot(string colour)
    {
        var bitmap = new Bitmap(12, 12);
        using var graphics = Graphics.FromImage(bitmap); graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        try { using var brush = new SolidBrush(ColorTranslator.FromHtml(colour)); graphics.FillEllipse(brush, 1, 1, 10, 10); } catch { }
        return bitmap;
    }

    static Icon LoadIcon()
    {
        using var iconStream = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/Assets/WinSwitch.ico")).Stream;
        return new Icon(iconStream, SystemInformation.SmallIconSize);
    }

    public void Dispose() { notifyIcon.Visible = false; notifyIcon.Dispose(); }
}
