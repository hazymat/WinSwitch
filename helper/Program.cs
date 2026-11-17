using System;
using System.Diagnostics;
using System.IO;
using System.ServiceProcess;
namespace WinSwitchHelper;

// WinSwitch Helper: a small LocalSystem service for the few things Windows only allows from admin (SPEC.md › Architecture).
// Built for .NET Framework 4.8, which every Windows 10/11 already has, so it is one small exe in an admin-only folder.
static class Program
{
    public const string ServiceName = "WinSwitchHelper";
    public static readonly string InstallFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WinSwitch Helper");
    public static readonly string InstalledExe = Path.Combine(InstallFolder, "WinSwitchHelper.exe");
    public static readonly string DataFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "WinSwitch");

    static int Main(string[] args)
    {
        var verb = args.Length > 0 ? args[0].ToLowerInvariant() : "";
        try
        {
            if (verb == "--install") return Install();
            if (verb == "--remove") { Commands.Restore(); ScheduleRemoval(); return 0; }
            if (verb == "--console") { var pipe = new PipeServer(); pipe.Start(); Console.WriteLine("WinSwitch Helper listening; Enter to stop"); Console.ReadLine(); pipe.Stop(); return 0; }
            ServiceBase.Run(new HelperService());
            return 0;
        }
        catch (Exception error) { Log.Write($"{verb} failed: {error}"); return 1; }
    }

    // Run elevated from a temporary copy: stop any older helper, copy into Program Files, register and start the service
    static int Install()
    {
        var existing = Find();
        if (existing != null && existing.Status != ServiceControllerStatus.Stopped) { try { existing.Stop(); } catch { } existing.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(20)); }
        Directory.CreateDirectory(InstallFolder);
        var self = Process.GetCurrentProcess().MainModule.FileName;
        if (!string.Equals(Path.GetFullPath(self), Path.GetFullPath(InstalledExe), StringComparison.OrdinalIgnoreCase)) CopyWithRetry(self, InstalledExe);
        if (existing == null)
        {
            Run("sc.exe", $"create {ServiceName} binPath= \"\\\"{InstalledExe}\\\"\" start= auto DisplayName= \"WinSwitch Helper\"");
            Run("sc.exe", $"description {ServiceName} \"Lets WinSwitch change device power, Wake on LAN, hibernate and services. Remove it from WinSwitch Settings > Helper.\"");
            Run("sc.exe", $"failure {ServiceName} reset= 86400 actions= restart/5000/restart/10000//");
        }
        using var service = Find() ?? throw new Exception("The service wasn't created");
        service.Start(); service.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(20));
        Log.Write($"Installed {Commands.Version}");
        return 0;
    }

    static void CopyWithRetry(string from, string to)
    {
        for (var attempt = 0; ; attempt++)
        {
            try { File.Copy(from, to, true); return; }
            catch (IOException) when (attempt < 10) { System.Threading.Thread.Sleep(500); } // the old service process may still be exiting
        }
    }

    public static ServiceController Find() { try { var service = new ServiceController(ServiceName); _ = service.Status; return service; } catch { return null; } }

    // The service can't delete its own exe, so a short-lived cmd waits for it to stop, then removes the service, its folder and the phone page's firewall rule
    public static void ScheduleRemoval()
    {
        Log.Write("Removing");
        var script = $"/c ping -n 3 127.0.0.1 >nul & sc stop {ServiceName} >nul & ping -n 4 127.0.0.1 >nul & sc delete {ServiceName} >nul & netsh advfirewall firewall delete rule name=\"WinSwitch phone page\" >nul & rmdir /s /q \"{InstallFolder}\"";
        Process.Start(new ProcessStartInfo("cmd.exe", script) { CreateNoWindow = true, UseShellExecute = false, WindowStyle = ProcessWindowStyle.Hidden });
    }

    public static (int exitCode, string output) Run(string file, string arguments, int timeoutMilliseconds = 60000)
    {
        using var process = Process.Start(new ProcessStartInfo(file, arguments) { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true });
        var output = process.StandardOutput.ReadToEndAsync(); var errors = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(timeoutMilliseconds)) { try { process.Kill(); } catch { } return (-1, "timed out"); }
        return (process.ExitCode, output.Result + errors.Result);
    }
}

sealed class HelperService : ServiceBase
{
    PipeServer pipe;
    public HelperService() { ServiceName = Program.ServiceName; CanStop = true; CanShutdown = true; }
    protected override void OnStart(string[] args) { Commands.CaptureOriginalOnce(); pipe = new PipeServer(); pipe.Start(); Log.Write($"Started {Commands.Version}"); }
    protected override void OnStop() { pipe?.Stop(); Log.Write("Stopped"); }
    protected override void OnShutdown() => OnStop();
}

static class Log
{
    static readonly object gate = new object();
    public static void Write(string line)
    {
        try
        {
            lock (gate)
            {
                Directory.CreateDirectory(Program.DataFolder);
                var path = Path.Combine(Program.DataFolder, "helper.log");
                if (File.Exists(path) && new FileInfo(path).Length > 1_000_000) File.Delete(path);
                File.AppendAllText(path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {line}\r\n");
            }
        }
        catch { }
    }
}
