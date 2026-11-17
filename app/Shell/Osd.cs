using System.Runtime.InteropServices;
using WinSwitch.Power;
namespace WinSwitch.Shell;

// Windows' own volume and brightness pop-up, for changes made remotely (Mat 17 Nov). Windows has no call for it, so:
// volume: a volume-up then volume-down key press (no net change) brings it up, then the exact level is set and the pop-up follows it;
// brightness: the shell's own "brightness changed" hook message (undocumented; if Windows ignores it nothing happens)
public static class Osd
{
    [DllImport("user32.dll")] static extern void keybd_event(byte key, byte scan, uint flags, UIntPtr extra);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindow(string? className, string? title);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern uint RegisterWindowMessage(string name);
    [DllImport("user32.dll")] static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    const byte VolumeDown = 0xAE, VolumeUp = 0xAF; const uint KeyUp = 0x2;

    public static bool SetVolume(int percent)
    {
        var muted = Direct.Muted() == true;
        foreach (var key in new[] { VolumeUp, VolumeDown }) { keybd_event(key, 0, 0, UIntPtr.Zero); keybd_event(key, 0, KeyUp, UIntPtr.Zero); }
        Thread.Sleep(60); // let Windows apply the two presses before the exact level
        var ok = Direct.SetVolume(percent);
        if (muted) Direct.SetMuted(true); // the volume key unmutes
        return ok;
    }

    // The mute key itself toggles and shows the pop-up
    public static bool SetMuted(bool muted)
    {
        if (Direct.Muted() == muted) return true;
        keybd_event(0xAD, 0, 0, UIntPtr.Zero); keybd_event(0xAD, 0, KeyUp, UIntPtr.Zero); Thread.Sleep(60);
        return Direct.Muted() == muted || Direct.SetMuted(muted);
    }

    public static bool SetBrightness(int percent)
    {
        var ok = Direct.SetBrightness(percent);
        try { var host = FindWindow("NativeHWNDHost", ""); if (host != IntPtr.Zero) PostMessage(host, RegisterWindowMessage("SHELLHOOK"), (IntPtr)0x37, IntPtr.Zero); } catch { }
        return ok;
    }
}
