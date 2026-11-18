using System.Runtime.InteropServices;
using System.Windows.Threading;
using WinSwitch.Apps;
using WinSwitch.Settings;
namespace WinSwitch.Shell;

// Other apps' windows left bigger than the screen they're on (dragged from the laptop screen to a smaller monitor, or after a dock change, e.g. Edison Mail).
// Asks first by default (Settings › General). Only ever makes a window smaller and keeps it on its own screen; maximised windows stay maximised and only their restore size is fitted
public static class WindowFitter
{
    static WinEventProc? onDragEnd; // kept alive for the hook
    static readonly DispatcherTimer displayTimer = new() { Interval = TimeSpan.FromSeconds(4) }; // Windows rearranges windows itself first
    static readonly HashSet<IntPtr> waiting = new(); // too big, waiting for an answer
    static AppSettings Settings => App.Instance.Settings;

    public static void Start()
    {
        onDragEnd = (hook, eventType, window, objectId, childId, thread, time) => { if (objectId == 0) _ = CheckAfterDragAsync(window); };
        SetWinEventHook(0x000B /* EVENT_SYSTEM_MOVESIZEEND */, 0x000B, IntPtr.Zero, onDragEnd, 0, 0, 0 /* WINEVENT_OUTOFCONTEXT */);
        displayTimer.Tick += (sender, args) => { displayTimer.Stop(); Check(AllWindows()); };
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += (sender, args) => App.Instance.Dispatcher.BeginInvoke(() => { displayTimer.Stop(); displayTimer.Start(); });
    }

    static async Task CheckAfterDragAsync(IntPtr window) { await Task.Delay(500); Check(new() { window }); } // the app rescales itself for the new screen first

    static void Check(List<IntPtr> windows)
    {
        if (Settings.General.FitWindows == "never") return;
        var tooBig = windows.Where(TooBig).ToList();
        if (tooBig.Count == 0) return;
        if (Settings.General.FitWindows == "always") { Fit(tooBig); return; }
        waiting.RemoveWhere(window => !TooBig(window)); waiting.UnionWith(tooBig);
        var names = waiting.Select(AppName).Distinct().ToList();
        var text = names.Count == 1 ? $"{names[0]} is bigger than the screen it's on." : $"{names[0]} and {names.Count - 1} other app{(names.Count == 2 ? "" : "s")} are bigger than their screens.";
        App.Instance.Notices.Ask("fitwindows", "Fit to the screen?", text, new()
        {
            (waiting.Count == 1 ? "Fit it" : "Fit them", () => { Fit(waiting.ToList()); waiting.Clear(); }),
            ("Always", () => { Settings.General.FitWindows = "always"; Settings.Save(); App.Instance.NotifySettingsChanged(); Fit(waiting.ToList()); waiting.Clear(); }),
            ("Not now", () => waiting.Clear()),
        });
    }

    // Settings › General › Fit windows now
    public static int FitAllNow() { var tooBig = AllWindows().Where(TooBig).ToList(); Fit(tooBig); return tooBig.Count; }

    // ---- Which windows: visible, resizable, another app's, not full screen, larger than their screen's work area or with the title bar above it ----
    static bool TooBig(IntPtr window)
    {
        try
        {
            if (!IsWindowVisible(window) || IsIconic(window) || GetWindow(window, 4 /* GW_OWNER */) != IntPtr.Zero || Cloaked(window)) return false;
            if ((GetWindowLong(window, -16 /* GWL_STYLE */) & 0x00040000 /* WS_THICKFRAME */) == 0 || (GetWindowLong(window, -20 /* GWL_EXSTYLE */) & 0x80 /* WS_EX_TOOLWINDOW */) != 0) return false;
            GetWindowThreadProcessId(window, out var processId); if (processId == Environment.ProcessId) return false;
            var (screen, work) = Screen(window);
            if (IsZoomed(window)) { var placement = Placement(window); return placement.NormalPosition.Width > work.Width + 2 || placement.NormalPosition.Height > work.Height + 2; }
            var frame = Frame(window);
            if (frame.Equals(screen)) return false; // borderless full screen (games, video)
            return frame.Width > work.Width + 2 || frame.Height > work.Height + 2 || frame.Top < screen.Top - 2;
        }
        catch { return false; }
    }

    static void Fit(List<IntPtr> windows)
    {
        foreach (var window in windows.Where(TooBig))
        {
            var (_, work) = Screen(window);
            if (IsZoomed(window))
            {
                // Restore size only, in workspace coordinates (relative to the main screen's work area)
                var placement = Placement(window); var normal = placement.NormalPosition; var origin = PrimaryWorkOrigin();
                var width = Math.Min(normal.Width, work.Width); var height = Math.Min(normal.Height, work.Height);
                var left = Math.Clamp(normal.Left + origin.X, work.Left, work.Right - width) - origin.X; var top = Math.Clamp(normal.Top + origin.Y, work.Top, work.Bottom - height) - origin.Y;
                placement.NormalPosition = new Rect { Left = left, Top = top, Right = left + width, Bottom = top + height };
                SetWindowPlacement(window, ref placement);
                continue;
            }
            var frame = Frame(window); GetWindowRect(window, out var outer); // the outer rect includes invisible resize borders
            var fitWidth = Math.Min(frame.Width, work.Width); var fitHeight = Math.Min(frame.Height, work.Height);
            var fitLeft = Math.Clamp(frame.Left, work.Left, work.Right - fitWidth); var fitTop = Math.Clamp(frame.Top, work.Top, work.Bottom - fitHeight);
            SetWindowPos(window, IntPtr.Zero, fitLeft - (frame.Left - outer.Left), fitTop - (frame.Top - outer.Top), fitWidth + (outer.Width - frame.Width), fitHeight + (outer.Height - frame.Height), 0x0004 | 0x0010 /* NOZORDER NOACTIVATE */);
        }
    }

    // ---- Win32 ----
    static List<IntPtr> AllWindows() { var windows = new List<IntPtr>(); EnumWindows((window, parameter) => { windows.Add(window); return true; }, IntPtr.Zero); return windows; }
    static Rect Frame(IntPtr window) => DwmGetWindowAttribute(window, 9 /* DWMWA_EXTENDED_FRAME_BOUNDS */, out Rect frame, Marshal.SizeOf<Rect>()) == 0 ? frame : GetWindowRect(window, out var outer) ? outer : default;
    static bool Cloaked(IntPtr window) => DwmGetWindowAttribute(window, 14 /* DWMWA_CLOAKED */, out int cloaked, sizeof(int)) == 0 && cloaked != 0;
    static (Rect screen, Rect work) Screen(IntPtr window) { var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() }; GetMonitorInfo(MonitorFromWindow(window, 2 /* NEAREST */), ref info); return (info.Monitor, info.Work); }
    static (int X, int Y) PrimaryWorkOrigin() { var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() }; GetMonitorInfo(MonitorFromWindow(IntPtr.Zero, 1 /* PRIMARY */), ref info); return (info.Work.Left, info.Work.Top); }
    static WindowPlacement Placement(IntPtr window) { var placement = new WindowPlacement { Length = Marshal.SizeOf<WindowPlacement>() }; GetWindowPlacement(window, ref placement); return placement; }
    static string AppName(IntPtr window) { GetWindowThreadProcessId(window, out var processId); return AppCatalog.ImagePath(processId) is { } path ? AppCatalog.FriendlyName(path) : "A window"; }

    [StructLayout(LayoutKind.Sequential)] struct Rect { public int Left, Top, Right, Bottom; public readonly int Width => Right - Left; public readonly int Height => Bottom - Top; }
    [StructLayout(LayoutKind.Sequential)] struct MonitorInfo { public int Size; public Rect Monitor, Work; public uint Flags; }
    [StructLayout(LayoutKind.Sequential)] struct WindowPlacement { public int Length, Flags, ShowCommand; public int MinX, MinY, MaxX, MaxY; public Rect NormalPosition; }
    delegate void WinEventProc(IntPtr hook, uint eventType, IntPtr window, int objectId, int childId, uint thread, uint time);
    delegate bool EnumWindowsProc(IntPtr window, IntPtr parameter);
    [DllImport("user32.dll")] static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr module, WinEventProc callback, uint processId, uint threadId, uint flags);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr window);
    [DllImport("user32.dll")] static extern bool IsZoomed(IntPtr window);
    [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr window, uint command);
    [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr window, int index);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr window, out int processId);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr window, out Rect rect);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] static extern bool GetWindowPlacement(IntPtr window, ref WindowPlacement placement);
    [DllImport("user32.dll")] static extern bool SetWindowPlacement(IntPtr window, ref WindowPlacement placement);
    [DllImport("user32.dll")] static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);
    [DllImport("user32.dll")] static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr window, int attribute, out Rect value, int size);
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr window, int attribute, out int value, int size);
}
