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

    public static void Start() { timer.Tick += (sender, args) => Tick(); timer.Start(); }

    static void Tick()
    {
        var lastInput = LastInput(); var idleSeconds = ((uint)Environment.TickCount - lastInput) / 1000;
        if (covers.Count > 0 && lastInput != blankedAtInput) { Uncover(true); return; }
        if (idleSeconds < 2) lockedThisTime = false;
        var pluggedIn = PowerApi.PowerStatus().pluggedIn;
        int Seconds(string key) => App.Instance.Engine.EffectiveValues.TryGetValue(key, out var pair) ? (pluggedIn ? pair.Ac : pair.Dc) ?? 0 : 0;
        var blankAfter = Seconds("blankAfter"); var lockAfter = Seconds("lockAfter");
        if (lockAfter > 0 && idleSeconds >= lockAfter && !lockedThisTime && (covers.Count > 0 || Allowed())) { lockedThisTime = true; Uncover(false); LockWorkStation(); return; }
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
        blankedAtInput = lastInput; coveredWindow = GetForegroundWindow();
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
        if (giveBack && coveredWindow != IntPtr.Zero) SetForegroundWindow(coveredWindow);
        timer.Interval = TimeSpan.FromSeconds(1);
    }

    static uint LastInput() { var info = new LastInputInfo { Size = (uint)Marshal.SizeOf<LastInputInfo>() }; return GetLastInputInfo(ref info) ? info.Time : (uint)Environment.TickCount; }

    [StructLayout(LayoutKind.Sequential)] struct LastInputInfo { public uint Size, Time; }
    [DllImport("user32.dll")] static extern bool GetLastInputInfo(ref LastInputInfo info);
    [DllImport("user32.dll")] static extern bool LockWorkStation();
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("shell32.dll")] static extern int SHQueryUserNotificationState(out int state);
    [DllImport("powrprof.dll")] static extern uint CallNtPowerInformation(int level, IntPtr input, int inputLength, out uint output, int outputLength);
}
