using System.Runtime.InteropServices;
using Microsoft.Win32;
using WinSwitch.Apps;
using WinSwitch.Settings;
namespace WinSwitch.Shell;

// Older apps that say they handle screen changes but don't (e.g. RDCMan): after undocking their text and title bar stay tiny (or huge) on the other screen.
// Nothing outside the app can make it redraw bigger, so WinSwitch offers Windows' own fix (Compatibility › Change high DPI settings › Override, scaling by System (Enhanced)), which applies from the app's next start
public static class DpiFixer
{
    const string LayersKey = @"Software\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers";
    const string Flags = "GDIDPISCALING DPIUNAWARE"; // System (Enhanced): Windows scales the app to each screen, text stays sharp
    static readonly HashSet<string> waiting = new(StringComparer.OrdinalIgnoreCase); // app paths, waiting for an answer
    static AppSettings Settings => App.Instance.Settings;

    // Called by WindowFitter after a display change or a drag between screens
    public static void Check(List<IntPtr> windows)
    {
        var found = windows.Select(Misscaled).Where(path => path != null && !Settings.General.DpiLeftAlone.Contains(path, StringComparer.OrdinalIgnoreCase) && !Overridden(path)).Cast<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (found.Count == 0) return;
        waiting.UnionWith(found);
        var names = waiting.Select(AppCatalog.FriendlyName).Distinct().ToList();
        var text = (names.Count == 1 ? $"{names[0]} doesn't resize itself" : $"{names[0]} and {names.Count - 1} other app{(names.Count == 2 ? "" : "s")} don't resize themselves") + " when moved to a screen with different scaling, so text looks tiny or huge. Let Windows scale it instead? Takes effect next time it opens.";
        App.Instance.Notices.Ask("dpifix", "Fix the text size?", text, new()
        {
            (waiting.Count == 1 ? "Fix it" : "Fix them", () => { foreach (var path in waiting) Override(path); waiting.Clear(); }),
            ("Not now", () => waiting.Clear()),
            ("Leave it", () => { Settings.General.DpiLeftAlone.AddRange(waiting); Settings.Save(); waiting.Clear(); }),
        });
    }

    // Another app's visible top-level window that is per-monitor aware v1 (handles screen changes itself, title bar never scaled) on a screen whose scaling differs from sign-in
    static string? Misscaled(IntPtr window)
    {
        try
        {
            if (!IsWindowVisible(window) || IsIconic(window) || GetWindow(window, 4 /* GW_OWNER */) != IntPtr.Zero) return null;
            if ((GetWindowLong(window, -16 /* GWL_STYLE */) & 0x00C00000 /* WS_CAPTION */) != 0x00C00000) return null;
            GetWindowThreadProcessId(window, out var processId); if (processId == Environment.ProcessId) return null;
            var context = GetWindowDpiAwarenessContext(window);
            if (GetAwarenessFromDpiAwarenessContext(context) != 2 /* PER_MONITOR */ || AreDpiAwarenessContextsEqual(context, new IntPtr(-4) /* PER_MONITOR_AWARE_V2 */)) return null;
            if (GetDpiForMonitor(MonitorFromWindow(window, 2 /* NEAREST */), 0 /* EFFECTIVE */, out var monitorDpi, out _) != 0 || monitorDpi == GetDpiForSystem()) return null;
            var path = AppCatalog.ImagePath(processId);
            return path != null && !path.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.Windows), StringComparison.OrdinalIgnoreCase) ? path : null; // Windows' own apps can't be overridden
        }
        catch { return null; }
    }

    static bool Overridden(string path) { using var key = Registry.CurrentUser.OpenSubKey(LayersKey); return (key?.GetValue(path) as string ?? "").Contains("DPIUNAWARE"); }

    // Keeps any other compatibility flags already set for the app
    static void Override(string path)
    {
        using var key = Registry.CurrentUser.CreateSubKey(LayersKey);
        var others = (key.GetValue(path) as string ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(flag => flag != "~" && flag is not ("HIGHDPIAWARE" or "DPIUNAWARE" or "GDIDPISCALING")).ToList();
        key.SetValue(path, "~ " + string.Join(" ", others.Append(Flags)));
    }

    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr window);
    [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr window, uint command);
    [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr window, int index);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr window, out int processId);
    [DllImport("user32.dll")] static extern IntPtr GetWindowDpiAwarenessContext(IntPtr window);
    [DllImport("user32.dll")] static extern int GetAwarenessFromDpiAwarenessContext(IntPtr context);
    [DllImport("user32.dll")] static extern bool AreDpiAwarenessContextsEqual(IntPtr first, IntPtr second);
    [DllImport("user32.dll")] static extern uint GetDpiForSystem();
    [DllImport("user32.dll")] static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);
    [DllImport("shcore.dll")] static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint dpiX, out uint dpiY);
}
