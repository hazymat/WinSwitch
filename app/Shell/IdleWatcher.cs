using System.Runtime.InteropServices;
using System.Windows.Threading;
using WinSwitch.Power;
namespace WinSwitch.Shell;

// The two "When I step away" stages Windows can't do (Mat 18 Nov): Blank (a black cover on every screen, gone at the first key or mouse move)
// and Lock at any point on the line. Dim, Screen off, Sleep and Hibernate are Windows' own timers, set through the plan
public static class IdleWatcher
{
    static readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(1) };
    static readonly List<System.Windows.Forms.Form> covers = new();
    static IntPtr coveredWindow; // the window in front before Blank, given back after
    static uint blankedAtInput; // the last input when Blank started: any newer input ends it
    static bool lockedThisTime;

    public static void Start()
    {
        timer.Tick += (sender, args) => Tick(); timer.Start();
        WatchDisplay(); StrayInput.Start();
        Microsoft.Win32.SystemEvents.SessionSwitch += (sender, args) => { if (args.Reason == Microsoft.Win32.SessionSwitchReason.SessionLock) Note(lockedThisTime && (DateTime.Now - lockedAt).TotalSeconds < 5 ? "Locked by WinSwitch" : "Locked by Windows"); };
        Microsoft.Win32.SystemEvents.PowerModeChanged += (sender, args) => { if (args.Mode == Microsoft.Win32.PowerModes.Suspend) Note("Sleep"); else if (args.Mode == Microsoft.Win32.PowerModes.Resume) Note("Woke"); };
    }

    // ---- What happened while you were away, for Test › Last time away (Mat 19 Nov: it locked at 4 minutes and nothing said why) ----
    public sealed record AwayEvent(int AtSeconds, string What);
    public sealed class Away { public DateTime Started; public int Seconds; public List<AwayEvent> Events = new(); }
    public static Away? Current { get; private set; }
    public static Away? Last { get; private set; }
    public static int IdleSeconds => (int)(((uint)Environment.TickCount - LastInput()) / 1000);
    public static int DisplayState { get; private set; } = 1; // 0 off, 1 on, 2 dimmed (Windows' console display state)
    public static bool Blanked => covers.Count > 0;
    static DateTime lockedAt;
    static void Note(string what) { if (Current != null && (Current.Events.Count == 0 || Current.Events[^1].What != what)) Current.Events.Add(new AwayEvent((int)(DateTime.Now - Current.Started).TotalSeconds, what)); }

    // Try a step › Blank: covers the screens now; the next key or mouse move ends it as usual
    public static void BlankNow(bool dark = false) { if (covers.Count > 0) return; Cover(LastInput()); if (dark && Direct.Brightness() is { } now) { brightnessBefore = now; Direct.SetBrightness(0); } }
    static int? brightnessBefore; // Screen off on Modern Standby: the laptop backlight, put back with the first key or mouse move

    // Windows' display state (on, dimmed, off) as it changes; the callback comes on a worker thread
    static PowerSettingCallback? onDisplay; // kept alive for Windows
    static void WatchDisplay()
    {
        try
        {
            onDisplay = (context, type, setting) =>
            {
                if (type == 0x8013 /* PBT_POWERSETTINGCHANGE */ && setting != IntPtr.Zero) { var state = Marshal.ReadInt32(setting, 20); App.Instance.Dispatcher.BeginInvoke(() => { DisplayState = state; Note(state == 0 ? "Screen off" : state == 2 ? "Dim" : "Screen on"); }); }
                return 0;
            };
            var parameters = new SubscribeParameters { Callback = onDisplay }; var displayState = new Guid("6fe69556-704a-47a0-8f24-c28d936fda47");
            PowerSettingRegisterNotification(ref displayState, 2 /* DEVICE_NOTIFY_CALLBACK */, ref parameters, out _);
        }
        catch { }
    }

    static void Tick()
    {
        var lastInput = LastInput(); var idleSeconds = ((uint)Environment.TickCount - lastInput) / 1000;
        if (covers.Count > 0 && lastInput != blankedAtInput) { Uncover(true); return; }
        if (idleSeconds >= 30 && Current == null) Current = new Away { Started = DateTime.Now.AddSeconds(-idleSeconds) };
        if (idleSeconds < 2 && Current != null) { Current.Seconds = (int)(DateTime.Now - Current.Started).TotalSeconds; Current.Events.RemoveAll(happened => happened.What == "Screen on" && happened.AtSeconds >= Current.Seconds - 3); if (Current.Seconds >= 60) Last = Current; Current = null; } // the screen coming on as you return isn't news
        if (idleSeconds < 2) lockedThisTime = false;
        var pluggedIn = PowerApi.PowerStatus().pluggedIn;
        int Seconds(string key) => App.Instance.Engine.EffectiveValues.TryGetValue(key, out var pair) ? (pluggedIn ? pair.Ac : pair.Dc) ?? 0 : 0;
        var blankAfter = Seconds("blankAfter"); var lockAfter = Seconds("lockAfter");
        if (lockAfter > 0 && idleSeconds >= lockAfter && !lockedThisTime && (covers.Count > 0 || Allowed())) { lockedThisTime = true; lockedAt = DateTime.Now; Uncover(false); LockWorkStation(); return; }
        if (covers.Count == 0 && blankAfter > 0 && idleSeconds >= blankAfter && !lockedThisTime && Allowed()) Cover(lastInput);
    }

    // Not while Keep awake is on, a presentation or full-screen game or video runs, or an app asks for the screen to stay on
    static bool Allowed()
    {
        if (PowerActions.Awake) return false;
        if (SHQueryUserNotificationState(out var state) == 0 && state is 3 or 4 or 5 /* busy full screen, Direct3D full screen, presentation */) return false;
        if (CallNtPowerInformation(16 /* SystemExecutionState */, IntPtr.Zero, 0, out var executionState, sizeof(uint)) == 0 && (executionState & 0x2 /* ES_DISPLAY_REQUIRED */) != 0) return false;
        return true;
    }

    static void Cover(uint lastInput)
    {
        blankedAtInput = lastInput; coveredWindow = GetForegroundWindow(); Note("Blank");
        foreach (var screen in System.Windows.Forms.Screen.AllScreens)
        {
            var cover = new System.Windows.Forms.Form { FormBorderStyle = System.Windows.Forms.FormBorderStyle.None, BackColor = System.Drawing.Color.Black, ShowInTaskbar = false, TopMost = true, StartPosition = System.Windows.Forms.FormStartPosition.Manual, Bounds = screen.Bounds, Cursor = System.Windows.Forms.Cursors.Default };
            cover.Show(); cover.Bounds = screen.Bounds; // again once shown: Windows may have scaled it for that screen
            covers.Add(cover);
        }
        covers[0].Activate(); // a key that wakes the screen goes to the cover, not into whatever you were typing in
        System.Windows.Forms.Cursor.Hide();
        timer.Interval = TimeSpan.FromMilliseconds(150);
    }

    static void Uncover(bool giveBack)
    {
        if (covers.Count == 0) return;
        foreach (var cover in covers) cover.Close();
        covers.Clear(); System.Windows.Forms.Cursor.Show();
        if (brightnessBefore is { } before) { brightnessBefore = null; Direct.SetBrightness(before); }
        if (giveBack && coveredWindow != IntPtr.Zero) SetForegroundWindow(coveredWindow);
        timer.Interval = TimeSpan.FromSeconds(1);
    }

    public static uint LastInput() { var info = new LastInputInfo { Size = (uint)Marshal.SizeOf<LastInputInfo>() }; return GetLastInputInfo(ref info) ? info.Time : (uint)Environment.TickCount; }

    delegate uint PowerSettingCallback(IntPtr context, int type, IntPtr setting);
    [StructLayout(LayoutKind.Sequential)] struct SubscribeParameters { public PowerSettingCallback Callback; public IntPtr Context; }
    [DllImport("powrprof.dll")] static extern uint PowerSettingRegisterNotification(ref Guid setting, uint flags, ref SubscribeParameters recipient, out IntPtr handle);
    [StructLayout(LayoutKind.Sequential)] struct LastInputInfo { public uint Size, Time; }
    [DllImport("user32.dll")] static extern bool GetLastInputInfo(ref LastInputInfo info);
    [DllImport("user32.dll")] static extern bool LockWorkStation();
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("shell32.dll")] static extern int SHQueryUserNotificationState(out int state);
    [DllImport("powrprof.dll")] static extern uint CallNtPowerInformation(int level, IntPtr input, int inputLength, out uint output, int outputLength);
}
