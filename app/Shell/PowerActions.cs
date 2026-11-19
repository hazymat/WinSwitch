using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using WinSwitch.Power;
namespace WinSwitch.Shell;

// Tray › Power and Keep awake (SPEC.md › Tray)
public static class PowerActions
{
    // Screen off from the tray or the phone. On Modern Standby PCs Windows treats "monitor off" as "go to standby" (Kernel-Power 506, reason SC_MONITORPOWER),
    // which stops music and the network; there it's a black cover with the laptop backlight off instead, and the PC stays fully on (Mat 19 Nov)
    public static void ScreenOff() { if (PowerApi.ModernStandby()) App.Instance.Dispatcher.BeginInvoke(() => IdleWatcher.BlankNow(dark: true)); else WindowsScreenOff(); }
    public static void WindowsScreenOff() => PostMessage((IntPtr)0xFFFF, 0x0112 /* WM_SYSCOMMAND */, (IntPtr)0xF170 /* SC_MONITORPOWER */, (IntPtr)2); // what Windows' own timer does
    public static void Lock() => LockWorkStation();
    public static void Sleep() => PowerApi.Sleep(false);
    public static void Hibernate() => PowerApi.Sleep(true);
    public static void Restart() => Process.Start(new ProcessStartInfo("shutdown.exe", "/r /t 0") { CreateNoWindow = true, UseShellExecute = false });
    public static void ShutDown() => Process.Start(new ProcessStartInfo("shutdown.exe", "/s /t 0") { CreateNoWindow = true, UseShellExecute = false });

    // Keep awake: no sleep and no screen off until the time runs out (or until turned off)
    static readonly DispatcherTimer awakeTimer = new();
    public static DateTime? AwakeUntil { get; private set; }
    public static bool Awake => AwakeUntil != null;
    public static event Action? AwakeChanged;
    public static void KeepAwake(TimeSpan? howLong)
    {
        SetThreadExecutionState(0x80000000 | 0x00000001 | 0x00000002); // ES_CONTINUOUS | ES_SYSTEM_REQUIRED | ES_DISPLAY_REQUIRED
        AwakeUntil = howLong == null ? DateTime.MaxValue : DateTime.Now + howLong.Value;
        awakeTimer.Stop();
        if (howLong != null) { awakeTimer.Interval = howLong.Value; awakeTimer.Tick -= OnAwakeEnded; awakeTimer.Tick += OnAwakeEnded; awakeTimer.Start(); }
        AwakeChanged?.Invoke();
    }
    static void OnAwakeEnded(object? sender, EventArgs args) => StopKeepingAwake();
    public static void StopKeepingAwake() { awakeTimer.Stop(); SetThreadExecutionState(0x80000000); AwakeUntil = null; AwakeChanged?.Invoke(); }

    [DllImport("user32.dll")] static extern bool PostMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] static extern bool LockWorkStation();
    [DllImport("kernel32.dll")] static extern uint SetThreadExecutionState(uint flags);
}
