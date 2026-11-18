using System.Drawing;
using System.Windows.Forms;
using WinSwitch.Power;
namespace WinSwitch.Shell;

// The whole everyday app: profiles, tweaks, Keep awake and Power, in the tray menu (SPEC.md › Tray). WinForms NotifyIcon runs fine on the WPF dispatcher
public sealed class TrayIcon : IDisposable
{
    readonly NotifyIcon notifyIcon;
    readonly ContextMenuStrip trayMenu = new();
    string iconKey = "";
    readonly Dictionary<int, IntPtr> iconFrames = new(); // pulse step -> icon
    Func<int, Bitmap>? drawIcon; IntPtr shownHandle;
    readonly System.Windows.Forms.Timer pulseTimer = new() { Interval = 125 }; int pulseStep;
    readonly System.Windows.Forms.Timer flashTimer = new() { Interval = 900 }; // update waiting: the icon until seen, the menu row whenever the menu is open
    bool menuOpen;
    bool flashOn = true;
    string seenUpdate = ""; // the ready version the user has seen explained; it flashes until then, then stays solid blue
    bool UpdateUnseen => App.Updater.ReadyVersion != "" && seenUpdate != App.Updater.ReadyVersion;
    ToolStripMenuItem? updateItem, messageItem; // flash while the menu is open
    readonly Image blueDot = Dot(Notices.UpdateColour), amberDot = Dot(Notices.QuestionColour), noDot = new Bitmap(64, 64);
    readonly System.Windows.Threading.DispatcherTimer batteryTimer = new() { Interval = TimeSpan.FromMinutes(1) }; // the fill follows the charge
    App App => App.Instance;

    public TrayIcon()
    {
        trayMenu.Opening += (sender, args) => BuildMenu();
        // Bold and small rows get their fonts once the menu is on its screen, from the menu's own font: fonts set earlier keep the main screen's scaling and come out tiny on a 200% screen
        trayMenu.Opened += (sender, args) =>
        {
            menuOpen = true; Refresh();
            var menuFont = trayMenu.Font;
            foreach (ToolStripItem item in trayMenu.Items) { if (item.Tag is "bold") item.Font = new Font(menuFont, FontStyle.Bold); else if (item.Tag is "small") item.Font = new Font(menuFont.FontFamily, menuFont.Size * 0.85f); }
        };
        notifyIcon = new NotifyIcon { Text = "WinSwitch", ContextMenuStrip = trayMenu, Visible = true };
        // Left-click explains a dot first (amber message card, then the blue update card), otherwise opens the same menu (the profiles are the point)
        notifyIcon.MouseUp += (sender, args) =>
        {
            if (args.Button != MouseButtons.Left) return;
            if (App.Notices.Unanswered.Count > 0) { App.Notices.ShowUnanswered(); return; }
            if (App.Updater.ReadyVersion != "") { ShowUpdateCard(); return; } // seen or not: the blue dot always explains itself
            typeof(NotifyIcon).GetMethod("ShowContextMenu", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)?.Invoke(notifyIcon, null);
        };
        trayMenu.Closed += (sender, args) => { menuOpen = false; if (updateItem != null) seenUpdate = App.Updater.ReadyVersion; Refresh(); }; // seen in the menu: the tray dot stops flashing
        App.Engine.Changed += Refresh; App.Notices.Changed += Refresh; App.Updater.Changed += Refresh; PowerActions.AwakeChanged += Refresh;
        batteryTimer.Tick += (sender, args) => Refresh(); batteryTimer.Start();
        pulseTimer.Tick += (sender, args) => { pulseStep = (pulseStep + 1) % PulseSteps; ShowIcon(); };
        flashTimer.Tick += (sender, args) => { flashOn = !flashOn; flashTimer.Interval = flashOn ? 900 : 500; if (menuOpen && updateItem != null) { updateItem.Image = flashOn ? blueDot : noDot; updateItem.Invalidate(); } if (menuOpen && messageItem != null) { messageItem.Image = flashOn ? amberDot : noDot; messageItem.Invalidate(); } Refresh(); };
        Microsoft.Win32.SystemEvents.UserPreferenceChanged += (sender, args) => Refresh(); // light or dark taskbar
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += (sender, args) => Refresh(); // taskbar scaling
        Refresh();
    }

    void BuildMenu()
    {
        trayMenu.Items.Clear();
        var engine = App.Engine; var settings = App.Settings;
        // The version, just the number, in a small shaded row at the top
        trayMenu.Items.Add(new ToolStripMenuItem(App.Updater.CurrentVersion) { Enabled = false, BackColor = Color.FromArgb(236, 236, 236), Tag = "small" });
        // The same dots as the tray icon, so a flashing blue or amber dot leads to its explanation (Mat 18 Nov)
        updateItem = null;
        if (App.Updater.ReadyVersion != "") { updateItem = Bold($"Update WinSwitch to {App.Updater.ReadyVersion}", async () => await App.Updater.RestartToUpdateAsync()); updateItem.Image = blueDot; trayMenu.Items.Add(updateItem); }
        messageItem = null;
        if (App.Notices.Unanswered.Count > 0) { var questionItem = messageItem = Bold(App.Notices.Unanswered.Count == 1 ? "Show message" : $"Show message ({App.Notices.Unanswered.Count} waiting)", App.Notices.ShowUnanswered); questionItem.Image = amberDot; trayMenu.Items.Add(questionItem); }
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
            if (item.Checked) item.Tag = "bold";
            trayMenu.Items.Add(item);
        }
        if (settings.Tweaks.Count > 0)
        {
            trayMenu.Items.Add(new ToolStripSeparator());
            foreach (var tweak in settings.Tweaks) { var on = engine.TweakIsOn(tweak); trayMenu.Items.Add(new ToolStripMenuItem(tweak.Name + (tweak.Scheduled ? $"  ({tweak.From} to {tweak.To})" : ""), Dot(tweak.Colour), (sender, args) => engine.SetTweak(tweak, !on)) { Checked = on }); }
        }
        // Remote control, its own group: what's on, and a way into its settings
        trayMenu.Items.Add(new ToolStripSeparator());
        var remote = App.Settings.Remote;
        var remoteParts = new List<string>(); if (remote.PhoneOn) remoteParts.Add(App.Phone.Running ? "phone page on" : App.Phone.OffOnThisNetwork ? "phone page off on this network" : "phone page has a problem"); if (remote.MqttOn) remoteParts.Add(App.Mqtt.Status.StartsWith("Connected") ? "MQTT connected" : "MQTT not connected");
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

    static ToolStripMenuItem Bold(string text, Action action) => new(text, null, (sender, args) => action()) { Tag = "bold" };
    // "24% charging, 7 hr 23 min until full" / "62% on battery, 3 hr 10 min left", like Windows' own battery tooltip. The time is left out while Windows has no charge rate yet
    static string BatteryLine(bool pluggedIn, int percent, bool charging, Windows.Devices.Power.BatteryReport? report)
    {
        if (pluggedIn && !charging) return $"{percent}% plugged in, " + (percent >= 100 ? "fully charged" : "not charging");
        var line = $"{percent}% " + (pluggedIn ? "charging" : "on battery");
        if (report == null) return line;
        double rate = report.ChargeRateInMilliwatts ?? 0, remaining = report.RemainingCapacityInMilliwattHours ?? 0, full = report.FullChargeCapacityInMilliwattHours ?? 0;
        var hours = pluggedIn && rate > 0 && full > remaining ? (full - remaining) / rate : !pluggedIn && rate < 0 && remaining > 0 ? remaining / -rate : 0;
        if (hours > 0 && hours < 48) line += $", {Duration(TimeSpan.FromHours(hours))} " + (pluggedIn ? "until full" : "left");
        return line;
    }
    static Windows.Devices.Power.BatteryReport? BatteryReport() { try { return Windows.Devices.Power.Battery.AggregateBattery.GetReport(); } catch { return null; } }
    static string Duration(TimeSpan time) => time.TotalMinutes < 60 ? $"{Math.Max(1, (int)time.TotalMinutes)} min" : $"{(int)time.TotalHours} hr {time.Minutes} min";

    static string AwakeLabel() => PowerActions.AwakeUntil == DateTime.MaxValue ? "Keeping awake" : $"Keeping awake until {PowerActions.AwakeUntil:HH:mm}";
    static bool Confirm(string question) => System.Windows.MessageBox.Show(question, "WinSwitch", System.Windows.MessageBoxButton.OKCancel, System.Windows.MessageBoxImage.Question) == System.Windows.MessageBoxResult.OK;

    // Hover text, and the battery icon with the profile's colour dot
    bool plugged;
    void Refresh() => App.Dispatcher.BeginInvoke(() =>
    {
        var engine = App.Engine; var (pluggedIn, percent, hasBattery) = PowerApi.PowerStatus();
        // Charging only when Windows says so: plugged in but held (charge limit, weak charger, full) shows a still, faint bolt instead (Mat 18 Nov)
        var report = hasBattery ? BatteryReport() : null; plugged = hasBattery && pluggedIn;
        var charging = plugged && percent < 100 && (report == null ? true : report.Status == Windows.System.Power.BatteryStatus.Charging);
        var state = App.Settings.Power.Paused ? "paused" : engine.ActiveProfile?.Name ?? "starting";
        // Anything a dot means comes first, with its dot, so hovering always says what it is (Mat 18 Nov); the tip is capped at 127 characters
        var notes = "";
        if (App.Notices.Unanswered.Count > 0) { var title = App.Notices.Unanswered[0].Title; notes += "\n🟠 " + (title.Length > 44 ? title[..43] + "…" : title) + (App.Notices.Unanswered.Count > 1 ? $" (+{App.Notices.Unanswered.Count - 1} more)" : ""); }
        if (App.Updater.ReadyVersion != "") notes += $"\n🔵 Update ready: {App.Updater.ReadyVersion}";
        var text = $"WinSwitch{notes}\nProfile: {state}" + (hasBattery ? "\n" + BatteryLine(pluggedIn, percent, charging, report) : "") + (PowerActions.Awake ? "\n" + AwakeLabel() : "");
        notifyIcon.Text = text.Length > 127 ? text[..127] : text;
        var colour = App.Settings.Power.Paused ? "" : engine.ActiveProfile?.Colour ?? "";
        var question = App.Notices.Unanswered.Count > 0; var light = TaskbarIsLight(); var size = TrayIconSize();
        // One dot: amber for a question, a slow blue flash while an update waits for a restart, otherwise the profile's colour
        var updateWaiting = App.Updater.ReadyVersion != ""; var updateUnseen = UpdateUnseen;
        // Amber flashes for as long as a message waits (Mat 18 Nov: the reference for WinGCal and WinSqueeze); blue flashes until the update has been seen, then stays solid
        var flashing = question || updateUnseen || menuOpen && updateItem != null;
        if (flashing != flashTimer.Enabled) { flashOn = true; if (flashing) flashTimer.Start(); else flashTimer.Stop(); }
        var dot = question ? (flashOn ? Notices.QuestionColour : "") : updateWaiting ? (!updateUnseen || flashOn ? Notices.UpdateColour : "") : colour;
        var level = hasBattery ? Math.Clamp(percent, 0, 100) / 5 * 5 : 100;
        if (charging != pulseTimer.Enabled) { pulseStep = 0; if (charging) pulseTimer.Start(); else pulseTimer.Stop(); } // the bolt breathes while charging (plugged in, so no battery cost)
        var key = $"{dot}|{light}|{size}|{level}|{plugged}|{charging}";
        if (key != iconKey) { iconKey = key; foreach (var handle in iconFrames.Values) DestroyIcon(handle); iconFrames.Clear(); drawIcon = step => BatteryIcon.Draw(size, level / 100.0, plugged, charging, dot, light, charging ? PulseStrength(step) : 1, step / (double)PulseSteps); }
        ShowIcon();
    });

    // Frames are drawn once per state and reused, so the pulse only swaps icons
    void ShowIcon()
    {
        if (drawIcon == null) return;
        var step = pulseTimer.Enabled ? pulseStep : 0;
        if (!iconFrames.TryGetValue(step, out var handle)) { using var bitmap = drawIcon(step); iconFrames[step] = handle = bitmap.GetHicon(); }
        if (handle == shownHandle) return;
        shownHandle = handle; notifyIcon.Icon = Icon.FromHandle(handle);
    }
    const int PulseSteps = 16; // 2 s a breath at 8 frames a second
    static double PulseStrength(int step) => 0.3 + 0.7 * (0.5 + 0.5 * Math.Cos(2 * Math.PI * step / PulseSteps));

    void ShowUpdateCard()
    {
        seenUpdate = App.Updater.ReadyVersion; Refresh();
        App.Notices.Info("Update ready", $"WinSwitch {App.Updater.ReadyVersion} is downloaded. The blue dot stays until you update.", () => _ = App.Updater.RestartToUpdateAsync(), "Update WinSwitch now", always: true, accent: Notices.UpdateColour);
    }

    // The size the taskbar shows tray icons at, from the taskbar's own scaling. SystemInformation.SmallIconSize is fixed at the scaling Windows had at sign-in, so after a dock change or restart the icon was drawn too small and blown up (fuzzy)
    static int TrayIconSize()
    {
        try { var taskbar = FindWindow("Shell_TrayWnd", null); var dpi = taskbar == IntPtr.Zero ? 0u : GetDpiForWindow(taskbar); if (dpi > 0) return GetSystemMetricsForDpi(49 /* SM_CXSMICON */, dpi); } catch { }
        return SystemInformation.SmallIconSize.Width;
    }
    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)] static extern IntPtr FindWindow(string className, string? windowName);
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern uint GetDpiForWindow(IntPtr window);
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern int GetSystemMetricsForDpi(int index, uint dpi);

    static bool TaskbarIsLight() { try { return Microsoft.Win32.Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "SystemUsesLightTheme", 0) is int value && value == 1; } catch { return false; } }
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr icon);

    static Image Dot(string colour)
    {
        var bitmap = new Bitmap(64, 64); // large and scaled down by the menu, so it stays sharp at any scaling
        using var graphics = Graphics.FromImage(bitmap); graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        try { using var brush = new SolidBrush(ColorTranslator.FromHtml(colour)); graphics.FillEllipse(brush, 8, 8, 48, 48); } catch { }
        return bitmap;
    }

    public void Dispose() { notifyIcon.Visible = false; notifyIcon.Dispose(); }
}
