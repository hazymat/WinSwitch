using Microsoft.Win32;
namespace WinSwitch.Settings;

// Per-user "Start with Windows" via HKCU Run, no admin rights
public static class StartupRegistration
{
    const string runKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string valueName = "WinSwitch";
    public static void Apply(bool startWithWindows)
    {
        using var runKey = Registry.CurrentUser.CreateSubKey(runKeyPath);
        if (startWithWindows) runKey.SetValue(valueName, $"\"{AppPaths.ExePath}\""); else runKey.DeleteValue(valueName, false);
    }
}
