using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
namespace WinSwitch.Shell;

// Bottom right of the main screen, above the clock. Worked out from real pixels: WPF's WorkArea uses one scaling for every screen, so with a 200% laptop screen and a 100% monitor a card landed mid-screen
public static class Corner
{
    // Moves the window onto the main screen first (so it takes that screen's scaling), then gives the work area's bottom-right in the window's own units
    public static Point BottomRight(Window window)
    {
        var area = System.Windows.Forms.Screen.PrimaryScreen?.WorkingArea ?? new System.Drawing.Rectangle(0, 0, (int)SystemParameters.WorkArea.Width, (int)SystemParameters.WorkArea.Height);
        var handle = new WindowInteropHelper(window).EnsureHandle();
        if (MonitorFromWindow(handle, 2 /* NEAREST */) != MonitorFromPoint(new NativePoint { X = area.Left, Y = area.Top }, 1 /* PRIMARY */)) SetWindowPos(handle, IntPtr.Zero, area.Left, area.Top, 0, 0, 0x0001 | 0x0004 | 0x0010 /* NOSIZE NOZORDER NOACTIVATE */);
        var scale = VisualTreeHelper.GetDpi(window).DpiScaleX;
        return new Point(area.Right / scale, area.Bottom / scale);
    }

    [StructLayout(LayoutKind.Sequential)] struct NativePoint { public int X, Y; }
    [DllImport("user32.dll")] static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);
    [DllImport("user32.dll")] static extern IntPtr MonitorFromPoint(NativePoint point, uint flags);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
}
