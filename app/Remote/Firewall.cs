using System.Diagnostics;
using WinSwitch.Settings;
namespace WinSwitch.Remote;

// A Windows Firewall rule so phones on the home network can reach the phone page: private networks only, this program and port only (SPEC.md › iPhone, no hub)
public static class Firewall
{
    const string RuleName = "WinSwitch phone page";

    public static async Task<bool> RuleExistsAsync()
    {
        try
        {
            using var netsh = Process.Start(new ProcessStartInfo("netsh", $"advfirewall firewall show rule name=\"{RuleName}\"") { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true })!;
            await netsh.StandardOutput.ReadToEndAsync(); await netsh.WaitForExitAsync();
            return netsh.ExitCode == 0;
        }
        catch { return false; }
    }

    // One admin prompt; replaces any older rule (e.g. another port)
    public static async Task<bool> AllowAsync(int port)
    {
        var commands = $"/c netsh advfirewall firewall delete rule name=\"{RuleName}\" & netsh advfirewall firewall add rule name=\"{RuleName}\" dir=in action=allow protocol=TCP localport={port} profile=private program=\"{AppPaths.ExePath}\"";
        try { using var elevated = Process.Start(new ProcessStartInfo("cmd.exe", commands) { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden })!; await elevated.WaitForExitAsync(); }
        catch { return false; } // permission declined
        return await RuleExistsAsync();
    }
}
