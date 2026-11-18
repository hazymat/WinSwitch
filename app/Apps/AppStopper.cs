using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.ServiceProcess;
using WinSwitch.Settings;
using WinSwitch.Shell;
namespace WinSwitch.Apps;

// Stops the apps a profile lists and starts them again on switching away, exactly as they were started (SPEC.md › Apps to stop)
public sealed class AppStopper
{
    App App => App.Instance;
    AppSettings Settings => App.Settings;
    HashSet<string>? lastWanted; // what the profile on now stops; null until the first evaluation
    SwitchPanel? panel;
    static readonly object consoleLock = new();

    public static HashSet<string> Wanted(Profile? profile, bool pluggedIn) => new((profile?.AppsToStop ?? new()).Where(app => !app.OnBatteryOnly || !pluggedIn).Select(app => app.Path), StringComparer.OrdinalIgnoreCase);

    // Runs on every evaluation and acts only when what should be stopped changes. True when the switching panel is showing
    public async Task<bool> ApplyAsync(Profile? profile, bool pluggedIn)
    {
        var wanted = Wanted(profile, pluggedIn);
        if (lastWanted == null)
        {
            // Stopped before this sign-in: Windows has started its own apps again, so starting them too would run two (two Syncthings fight over one database). Forget them; the profile stops them afresh
            var signedInAt = SignedInAt();
            if (Settings.Power.Stopped.RemoveAll(app => app.StoppedAt < signedInAt) > 0) Settings.Save();
            lastWanted = new HashSet<string>(Settings.Power.Stopped.Select(app => app.Path), StringComparer.OrdinalIgnoreCase); // after WinSwitch restarts (an update), what was stopped counts as handled
        }
        var newlyWanted = wanted.Where(path => !lastWanted.Contains(path)).ToList();
        var toStart = Settings.Power.Stopped.Where(app => !wanted.Contains(app.Path)).ToList();
        lastWanted = wanted;
        if (newlyWanted.Count == 0 && toStart.Count == 0) return false;
        var running = await Task.Run(AppCatalog.RunningByPath);
        foreach (var app in toStart.Where(app => running.ContainsKey(app.Path)).ToList()) { toStart.Remove(app); Forget(app.Path); Log($"{app.Name} was already running again"); } // started by hand meanwhile: never a second copy
        foreach (var app in toStart.Where(app => ComesBackWithParent(app, toStart, running)).ToList()) { toStart.Remove(app); Forget(app.Path); Log($"{app.Name} comes back with {Path.GetFileNameWithoutExtension(app.StartedBy)}"); }
        var toStop = newlyWanted.Where(running.ContainsKey).ToList();
        if (toStop.Count == 0 && toStart.Count == 0) return false;
        var starts = await Task.Run(() => AppCatalog.StartsWithWindows(includeAllTasks: true)); // to tell how each one was started
        panel?.KeepAllAndClose();
        var thisPanel = panel = new SwitchPanel(profile == null ? "Windows' own settings" : profile.Name);
        thisPanel.Closed += (sender, args) => { if (panel == thisPanel) panel = null; };
        thisPanel.Show();
        var stoppedNow = new List<StoppedApp>();
        var work = new List<Task>();
        foreach (var app in toStart)
        {
            var row = thisPanel.AddLine(app.Path, $"Starting {app.Name} again");
            work.Add(Task.Run(async () => { var started = await StartAgainAsync(app); App.Dispatcher.Invoke(() => { Forget(app.Path); row.Done(started ? $"{app.Name} started again" : $"{app.Name} couldn't be started again", started); }); }));
        }
        foreach (var path in toStop)
        {
            var processes = running[path];
            var root = processes.FirstOrDefault(process => !processes.Any(other => other.ProcessId == process.ParentId)) ?? processes[0];
            var record = AppCatalog.Describe(root, starts);
            record.StartedBy = wanted.FirstOrDefault(other => !other.Equals(path, StringComparison.OrdinalIgnoreCase) && running.TryGetValue(other, out var parents) && parents.Any(parent => parent.ProcessId == root.ParentId)) ?? "";
            if (processes.Any(process => process.HasWindow) && Settings.General.AskBeforeClosing)
            {
                thisPanel.AddPrompt(path, $"{record.Name} is open. Close it?", async row =>
                {
                    row.Working($"Closing {record.Name}…");
                    var result = await StopAsync(processes, record, mayForce: false);
                    if (result == StopResult.StillOpen) row.Ask($"{record.Name} is still open. It may be asking you something.", ("End it", async () => { row.Working($"Ending {record.Name}…"); Finish(row, record, await StopAsync(processes, record, mayForce: true), stoppedNow); }), ("Leave it", () => { row.Done($"Kept {record.Name}", true); return Task.CompletedTask; }));
                    else Finish(row, record, result, stoppedNow);
                }, row => row.Done($"Kept {record.Name}", true));
                continue;
            }
            var line = thisPanel.AddLine(path, $"Stopping {record.Name}…");
            work.Add(Task.Run(async () => { var result = await StopAsync(processes, record, mayForce: !processes.Any(process => process.HasWindow)); App.Dispatcher.Invoke(() => { if (result == StopResult.StillOpen) line.Ask($"{record.Name} is still open. It may be asking you something.", ("End it", async () => { line.Working($"Ending {record.Name}…"); Finish(line, record, await StopAsync(processes, record, mayForce: true), stoppedNow); }), ("Leave it", () => { line.Done($"Kept {record.Name}", true); return Task.CompletedTask; })); else Finish(line, record, result, stoppedNow); }); }));
        }
        // The engine carries on; the panel sums up once the background apps are done and every question is answered
        _ = Task.WhenAll(work).ContinueWith(finished => App.Dispatcher.Invoke(() => thisPanel.WhenSettled(() =>
        {
            var summary = stoppedNow.Count == 0 ? "" : stoppedNow.Count == 1 ? $"Stopped {stoppedNow[0].Name}" : $"Stopped {stoppedNow[0].Name} and {stoppedNow.Count - 1} other{(stoppedNow.Count == 2 ? "" : "s")}";
            if (summary != "" && !pluggedIn) summary += " to save battery";
            thisPanel.Finish(summary, stoppedNow.Count == 0 ? null : () => _ = StartTheseAgainAsync(stoppedNow.ToList()));
        })));
        return true;
    }

    void Finish(SwitchPanel.Row row, StoppedApp record, StopResult result, List<StoppedApp> stoppedNow)
    {
        if (result == StopResult.Stopped) { Remember(record); stoppedNow.Add(record); row.Done($"Stopped {record.Name}", true); }
        else row.Done(result == StopResult.NeedsHelper ? $"{record.Name} needs the helper to be stopped (Settings › Helper)" : $"{record.Name} couldn't be stopped", false);
    }

    void Remember(StoppedApp record) { Forget(record.Path, save: false); Settings.Power.Stopped.Add(record); Settings.Save(); Log($"Stopped {record.Name} ({record.How}: {record.Target})"); }
    void Forget(string path, bool save = true) { Settings.Power.Stopped.RemoveAll(app => app.Path.Equals(path, StringComparison.OrdinalIgnoreCase)); if (save) Settings.Save(); }

    // "Start them now" on the panel, and Restore everything: started again and not stopped again until the next switch
    public async Task StartTheseAgainAsync(List<StoppedApp> apps)
    {
        var running = await Task.Run(AppCatalog.RunningByPath);
        foreach (var app in apps) { if (!running.ContainsKey(app.Path) && !ComesBackWithParent(app, apps, running)) await StartAgainAsync(app); Forget(app.Path); }
    }

    // Launched by another app being started too, or already running (SyncTrayzor's Syncthing): starting it as well would run two, and the second can't open Syncthing's database
    static bool ComesBackWithParent(StoppedApp app, List<StoppedApp> starting, Dictionary<string, List<AppCatalog.RunningProcess>> running) => app.StartedBy != "" && (running.ContainsKey(app.StartedBy) || starting.Any(other => other.Path.Equals(app.StartedBy, StringComparison.OrdinalIgnoreCase)));
    public Task StartEverythingAgainAsync() { lastWanted = new(StringComparer.OrdinalIgnoreCase); return StartTheseAgainAsync(Settings.Power.Stopped.ToList()); }

    // ---- Stopping: asked to close the way Windows does (WM_CLOSE to its windows, Ctrl+C to console apps), then ended after 10 s ----
    public enum StopResult { Stopped, StillOpen, Failed, NeedsHelper }

    public static async Task<StopResult> StopAsync(List<AppCatalog.RunningProcess> processes, StoppedApp record, bool mayForce)
    {
        if (record.How == "service") return await StopServiceAsync(record.Target) ? StopResult.Stopped : StopResult.NeedsHelper;
        await Task.Run(() => { foreach (var process in processes) AskToClose(process.ProcessId); });
        if (await WaitForExitAsync(processes, TimeSpan.FromSeconds(10))) return StopResult.Stopped;
        if (!mayForce) return StopResult.StillOpen;
        var needsHelper = false;
        foreach (var process in processes)
        {
            try { using var running = Process.GetProcessById(process.ProcessId); if (!running.HasExited) running.Kill(entireProcessTree: true); }
            catch (ArgumentException) { } // already gone
            catch (Exception) { if (!await HelperClient.DoAsync("end-process", new { processId = process.ProcessId })) needsHelper = true; }
        }
        if (await WaitForExitAsync(processes, TimeSpan.FromSeconds(5))) return StopResult.Stopped;
        return needsHelper ? StopResult.NeedsHelper : StopResult.Failed;
    }

    static void AskToClose(int processId)
    {
        var windows = AppCatalog.WindowsOf(processId);
        if (windows.Count > 0) { foreach (var window in windows) PostMessage(window, 0x0010 /* WM_CLOSE */, IntPtr.Zero, IntPtr.Zero); return; }
        // A console app (Syncthing): Ctrl+C on its console, the same as pressing it there. One console at a time per process, so one at a time
        lock (consoleLock)
        {
            FreeConsole();
            if (!AttachConsole((uint)processId)) return;
            SetConsoleCtrlHandler(IntPtr.Zero, true); // so WinSwitch itself ignores it
            GenerateConsoleCtrlEvent(0 /* CTRL_C_EVENT */, 0);
            Thread.Sleep(500);
            FreeConsole();
            SetConsoleCtrlHandler(IntPtr.Zero, false);
        }
    }

    static async Task<bool> WaitForExitAsync(List<AppCatalog.RunningProcess> processes, TimeSpan howLong)
    {
        var until = DateTime.Now + howLong;
        while (DateTime.Now < until)
        {
            if (processes.All(process => HasExited(process.ProcessId))) return true;
            await Task.Delay(250);
        }
        return processes.All(process => HasExited(process.ProcessId));
    }
    static bool HasExited(int processId) { try { using var process = Process.GetProcessById(processId); return process.HasExited; } catch { return true; } }

    static async Task<bool> StopServiceAsync(string serviceName)
    {
        try
        {
            using var service = new ServiceController(serviceName);
            if (service.Status == ServiceControllerStatus.Stopped) return true;
            service.Stop(); service.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(20));
            return true;
        }
        catch { return await HelperClient.DoAsync("service", new { name = serviceName, start = false }); }
    }

    // ---- Starting again, the way it was started: the service, the scheduled task, the Startup shortcut, or the same command line ----
    public static async Task<bool> StartAgainAsync(StoppedApp app)
    {
        try
        {
            switch (app.How)
            {
                case "service":
                    try { using var service = new ServiceController(app.Target); if (service.Status != ServiceControllerStatus.Running) service.Start(); }
                    catch { return await HelperClient.DoAsync("service", new { name = app.Target, start = true }); }
                    break;
                case "task":
                    try { dynamic scheduler = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service")!)!; scheduler.Connect(); var folderPath = Path.GetDirectoryName(app.Target) ?? @"\"; scheduler.GetFolder(folderPath).GetTask(Path.GetFileName(app.Target)).Run(null); }
                    catch { return await HelperClient.DoAsync("run-task", new { path = app.Target }); }
                    break;
                case "shortcut":
                    Process.Start(new ProcessStartInfo(app.Target) { UseShellExecute = true, Verb = app.Elevated ? "runas" : "" })?.Dispose();
                    break;
                default:
                    var startInfo = new ProcessStartInfo(app.Path, AppCatalog.Arguments(app.Target)) { WorkingDirectory = Directory.Exists(app.WorkingFolder) ? app.WorkingFolder : Path.GetDirectoryName(app.Path) ?? "", UseShellExecute = app.Elevated };
                    if (app.Elevated) startInfo.Verb = "runas"; // asks Windows for permission, the same as starting it as admin by hand
                    Process.Start(startInfo)?.Dispose();
                    break;
            }
            Log($"Started {app.Name} again ({app.How})");
            return true;
        }
        catch (Win32Exception error) when (error.NativeErrorCode == 1223) { Log($"Didn't start {app.Name}: permission declined"); return false; }
        catch (Exception error) { Log($"Couldn't start {app.Name} again: {error.Message}"); return false; }
    }

    // Uninstall and Restore everything: synchronous, without the app running
    public static void StartAllAgain(AppSettings settings)
    {
        var stopped = settings.Power.Stopped.ToList(); var running = AppCatalog.RunningByPath();
        foreach (var app in stopped.Where(app => !running.ContainsKey(app.Path) && !ComesBackWithParent(app, stopped, running))) { try { StartAgainAsync(app).Wait(TimeSpan.FromSeconds(20)); } catch { } }
        settings.Power.Stopped.Clear(); settings.Save();
    }

    // When this Windows sign-in began: the later of boot and this session's own shell starting (Fast startup keeps the boot clock running across shut downs)
    static DateTime SignedInAt()
    {
        var bootedAt = DateTime.Now - TimeSpan.FromMilliseconds(Environment.TickCount64);
        var session = Process.GetCurrentProcess().SessionId; var shellStarts = new List<DateTime>();
        foreach (var name in new[] { "sihost", "explorer" }) foreach (var process in Process.GetProcessesByName(name)) using (process) { try { if (process.SessionId == session) shellStarts.Add(process.StartTime); } catch { } }
        return shellStarts.Count > 0 && shellStarts.Min() > bootedAt ? shellStarts.Min() : bootedAt;
    }

    static void Log(string line) { try { File.AppendAllText(Path.Combine(AppPaths.LogsFolder, "apps.log"), $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {line}{Environment.NewLine}"); } catch { } }

    [DllImport("user32.dll")] static extern bool PostMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll")] static extern bool AttachConsole(uint processId);
    [DllImport("kernel32.dll")] static extern bool FreeConsole();
    [DllImport("kernel32.dll")] static extern bool SetConsoleCtrlHandler(IntPtr handler, bool add);
    [DllImport("kernel32.dll")] static extern bool GenerateConsoleCtrlEvent(uint controlEvent, uint processGroupId);
}
