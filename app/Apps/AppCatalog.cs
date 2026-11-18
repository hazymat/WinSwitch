using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Management;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;
using WinSwitch.Settings;
namespace WinSwitch.Apps;

// What is running and what starts with Windows, and how each program was started, so it can be started again the same way (SPEC.md › Apps to stop)
public static class AppCatalog
{
    // The few that would take Windows down on the spot; everything else may be stopped (Mat's call)
    public static readonly HashSet<string> Untouchable = new(StringComparer.OrdinalIgnoreCase) { "csrss", "wininit", "winlogon", "lsass", "smss", "services", "System", "Idle", "Registry", "Secure System", "MemCompression" };

    public record RunningProcess(int ProcessId, int ParentId, string Path, string CommandLine, bool HasWindow);
    public record Choice(string Path, string Name, string Group, string Detail, bool HasWindow);
    public record StartEntry(string Path, string Name, string How, string Target, string CommandLine, string WorkingFolder); // How: shortcut | run | task | service

    // ---- Running now, one row per program (path) ----
    public static Dictionary<string, List<RunningProcess>> RunningByPath()
    {
        var windowed = WindowedProcessIds();
        var byPath = new Dictionary<string, List<RunningProcess>>(StringComparer.OrdinalIgnoreCase);
        var ownId = Environment.ProcessId;
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT ProcessId, ParentProcessId, Name, ExecutablePath, CommandLine FROM Win32_Process");
            foreach (ManagementObject process in searcher.Get()) using (process)
            {
                var processId = Convert.ToInt32(process["ProcessId"]);
                var name = Path.GetFileNameWithoutExtension(process["Name"] as string ?? "");
                if (processId == ownId || processId <= 4 || Untouchable.Contains(name)) continue;
                var path = process["ExecutablePath"] as string ?? ImagePath(processId);
                if (string.IsNullOrEmpty(path)) continue;
                if (!byPath.TryGetValue(path, out var list)) byPath[path] = list = new();
                list.Add(new RunningProcess(processId, Convert.ToInt32(process["ParentProcessId"]), path, process["CommandLine"] as string ?? "", windowed.Contains(processId)));
            }
        }
        catch (Exception error) { Shell.CrashReporter.Write("Listing running apps", error, false); }
        return byPath;
    }

    // The combined chooser list: Running now (windows first), then Starts with Windows (SPEC.md › Apps to stop)
    public static List<Choice> Choices()
    {
        var choices = new List<Choice>();
        var running = RunningByPath();
        var starts = StartsWithWindows();
        var howByPath = starts.GroupBy(entry => entry.Path, StringComparer.OrdinalIgnoreCase).ToDictionary(group => group.Key, group => HowWords(group.First()), StringComparer.OrdinalIgnoreCase);
        foreach (var (path, processes) in running.OrderByDescending(entry => entry.Value.Any(process => process.HasWindow)).ThenBy(entry => FriendlyName(entry.Key)))
        {
            if (path.Equals(AppPaths.ExePath, StringComparison.OrdinalIgnoreCase)) continue;
            var hasWindow = processes.Any(process => process.HasWindow);
            choices.Add(new Choice(path, FriendlyName(path), "Running now", (hasWindow ? "Open in a window" : "In the background") + (howByPath.TryGetValue(path, out var how) ? ", " + how : ""), hasWindow));
        }
        foreach (var entry in starts.Where(entry => !running.ContainsKey(entry.Path)).GroupBy(entry => entry.Path, StringComparer.OrdinalIgnoreCase).Select(group => group.First()).OrderBy(entry => entry.Name))
            choices.Add(new Choice(entry.Path, entry.Name, "Starts with Windows", "Not running now, " + HowWords(entry), false));
        return choices;
    }

    static string HowWords(StartEntry entry) => entry.How switch { "service" => "a service", "task" => "a scheduled task", "shortcut" => "Startup folder", _ => "starts with Windows" };

    // ---- Starts with Windows: Run keys, Startup folders, logon and boot tasks, automatic services ----
    public static List<StartEntry> StartsWithWindows(bool includeAllTasks = false)
    {
        var entries = new List<StartEntry>();
        foreach (var (hive, keyPath) in new[] { (Registry.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Run"), (Registry.LocalMachine, @"Software\Microsoft\Windows\CurrentVersion\Run"), (Registry.LocalMachine, @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Run") })
        {
            try
            {
                using var key = hive.OpenSubKey(keyPath);
                foreach (var valueName in key?.GetValueNames() ?? Array.Empty<string>())
                    if (key!.GetValue(valueName) is string commandLine && ExeOf(commandLine) is { } path) entries.Add(new StartEntry(path, valueName == "" ? FriendlyName(path) : valueName, "run", commandLine, commandLine, ""));
            }
            catch { }
        }
        foreach (var folder in new[] { Environment.GetFolderPath(Environment.SpecialFolder.Startup), Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup) })
        {
            try
            {
                foreach (var shortcut in Directory.GetFiles(folder, "*.lnk"))
                    if (ReadShortcut(shortcut) is { } link && link.target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) entries.Add(new StartEntry(link.target, Path.GetFileNameWithoutExtension(shortcut), "shortcut", shortcut, Quote(link.target) + (link.arguments == "" ? "" : " " + link.arguments), link.workingFolder));
            }
            catch { }
        }
        entries.AddRange(Tasks(includeAllTasks));
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT Name, DisplayName, PathName FROM Win32_Service WHERE StartMode='Auto'");
            foreach (ManagementObject service in searcher.Get()) using (service)
                if (ExeOf(service["PathName"] as string ?? "") is { } path && !Path.GetFileName(path).Equals("svchost.exe", StringComparison.OrdinalIgnoreCase)) entries.Add(new StartEntry(path, service["DisplayName"] as string ?? (string)service["Name"], "service", (string)service["Name"], "", ""));
        }
        catch { }
        return entries;
    }

    // Scheduled tasks that run a program (Task Scheduler COM); outside \Microsoft\ unless asked, which is Windows' own and very long
    static List<StartEntry> Tasks(bool includeAll)
    {
        var entries = new List<StartEntry>();
        try
        {
            dynamic scheduler = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service")!)!;
            scheduler.Connect();
            void Walk(dynamic folder)
            {
                string folderPath = folder.Path;
                if (!includeAll && folderPath.StartsWith(@"\Microsoft", StringComparison.OrdinalIgnoreCase)) return;
                foreach (dynamic task in folder.GetTasks(1 /* include hidden */))
                {
                    try
                    {
                        dynamic definition = task.Definition;
                        var atStart = includeAll; foreach (dynamic trigger in definition.Triggers) if ((int)trigger.Type is 8 /* boot */ or 9 /* logon */) atStart = true;
                        if (!atStart) continue;
                        foreach (dynamic action in definition.Actions)
                        {
                            if ((int)action.Type != 0) continue;
                            string program = Environment.ExpandEnvironmentVariables(((string)action.Path ?? "").Trim('"'));
                            if (!program.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;
                            if (!Path.IsPathRooted(program) && FindOnPath(program) is { } found) program = found;
                            entries.Add(new StartEntry(program, (string)task.Name, "task", (string)task.Path, Quote(program) + " " + (string)(action.Arguments ?? ""), (string)(action.WorkingDirectory ?? "")));
                        }
                    }
                    catch { }
                }
                foreach (dynamic child in folder.GetFolders(0)) Walk(child);
            }
            Walk(scheduler.GetFolder(@"\"));
        }
        catch { }
        return entries;
    }

    // ---- How was this process started? Service first, then a task, then a Startup shortcut or Run key, else its own command line ----
    public static StoppedApp Describe(RunningProcess process, List<StartEntry> starts)
    {
        var stopped = new StoppedApp { Path = process.Path, Name = FriendlyName(process.Path), Elevated = IsElevatedOrOther(process.ProcessId), WorkingFolder = Path.GetDirectoryName(process.Path) ?? "" };
        try
        {
            using var searcher = new ManagementObjectSearcher($"SELECT Name FROM Win32_Service WHERE ProcessId={process.ProcessId}");
            foreach (ManagementObject service in searcher.Get()) using (service) { stopped.How = "service"; stopped.Target = (string)service["Name"]; return stopped; }
        }
        catch { }
        var match = starts.Where(entry => entry.How != "service" && entry.Path.Equals(process.Path, StringComparison.OrdinalIgnoreCase)).OrderBy(entry => entry.How == "task" ? 0 : entry.How == "shortcut" ? 1 : 2).FirstOrDefault();
        if (match?.How == "task") { stopped.How = "task"; stopped.Target = match.Target; return stopped; }
        if (match?.How == "shortcut" && SameArguments(match.CommandLine, process.CommandLine)) { stopped.How = "shortcut"; stopped.Target = match.Target; return stopped; }
        stopped.How = "command";
        stopped.Target = process.CommandLine != "" ? process.CommandLine : match?.CommandLine ?? Quote(process.Path);
        if (match != null && match.WorkingFolder != "") stopped.WorkingFolder = match.WorkingFolder;
        return stopped;
    }
    static bool SameArguments(string first, string second) => Arguments(first).Trim() == Arguments(second).Trim();

    // ---- Helpers: names, paths, command lines ----
    public static string FriendlyName(string path)
    {
        try { var description = FileVersionInfo.GetVersionInfo(path).FileDescription?.Trim(); if (!string.IsNullOrEmpty(description) && description.Length < 60) return description; } catch { }
        return Path.GetFileNameWithoutExtension(path);
    }

    static string Quote(string path) => path.Contains(' ') ? $"\"{path}\"" : path;

    // The program in a command line: quoted, or unquoted with spaces (tried word by word until a file exists)
    public static string? ExeOf(string commandLine)
    {
        commandLine = Environment.ExpandEnvironmentVariables(commandLine.Trim());
        if (commandLine == "") return null;
        if (commandLine.StartsWith('"')) { var end = commandLine.IndexOf('"', 1); var quoted = end > 1 ? commandLine[1..end] : commandLine.Trim('"'); return quoted.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? Full(quoted) : null; }
        var exeEnd = commandLine.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        while (exeEnd > 0)
        {
            var candidate = commandLine[..(exeEnd + 4)];
            if (File.Exists(candidate) || !candidate.Contains('\\')) return Full(candidate);
            exeEnd = commandLine.IndexOf(".exe", exeEnd + 4, StringComparison.OrdinalIgnoreCase);
        }
        return null;
        static string Full(string path) => Path.IsPathRooted(path) ? path : FindOnPath(path) ?? path;
    }

    // Everything after the program in a command line, exactly as written (so switches stay the same)
    public static string Arguments(string commandLine)
    {
        commandLine = commandLine.TrimStart();
        if (commandLine.StartsWith('"')) { var end = commandLine.IndexOf('"', 1); return end < 0 ? "" : commandLine[(end + 1)..].TrimStart(); }
        var exeEnd = commandLine.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        if (exeEnd >= 0) return commandLine[(exeEnd + 4)..].TrimStart();
        var space = commandLine.IndexOf(' ');
        return space < 0 ? "" : commandLine[(space + 1)..];
    }

    static string? FindOnPath(string fileName)
    {
        foreach (var folder in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';').Prepend(Environment.SystemDirectory))
            try { var candidate = Path.Combine(folder.Trim(), fileName); if (File.Exists(candidate)) return candidate; } catch { }
        return null;
    }

    static (string target, string arguments, string workingFolder)? ReadShortcut(string shortcutFile)
    {
        try
        {
            dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!)!;
            dynamic link = shell.CreateShortcut(shortcutFile);
            return ((string)link.TargetPath, (string)link.Arguments, (string)link.WorkingDirectory);
        }
        catch { return null; }
    }

    // Processes with a visible top-level window (what a person would call "open")
    public static HashSet<int> WindowedProcessIds()
    {
        var ids = new HashSet<int>();
        EnumWindows((window, parameter) =>
        {
            if (IsWindowVisible(window) && GetWindow(window, 4 /* GW_OWNER */) == IntPtr.Zero && GetWindowTextLength(window) > 0 && (GetWindowLong(window, -20 /* GWL_EXSTYLE */) & 0x80 /* WS_EX_TOOLWINDOW */) == 0)
            { GetWindowThreadProcessId(window, out var processId); ids.Add((int)processId); }
            return true;
        }, IntPtr.Zero);
        return ids;
    }

    // Every top-level window of a process, hidden ones too (tray apps keep a hidden one that answers WM_CLOSE)
    public static List<IntPtr> WindowsOf(int processId)
    {
        var windows = new List<IntPtr>();
        EnumWindows((window, parameter) => { GetWindowThreadProcessId(window, out var owner); if (owner == processId) windows.Add(window); return true; }, IntPtr.Zero);
        return windows;
    }

    public static string? ImagePath(int processId)
    {
        var handle = OpenProcess(0x1000 /* PROCESS_QUERY_LIMITED_INFORMATION */, false, processId);
        if (handle == IntPtr.Zero) return null;
        try { var builder = new StringBuilder(1024); var size = builder.Capacity; return QueryFullProcessImageName(handle, 0, builder, ref size) ? builder.ToString() : null; }
        finally { CloseHandle(handle); }
    }

    // Elevated, or another account's: either way it needs admin to start again as it was
    public static bool IsElevatedOrOther(int processId)
    {
        var handle = OpenProcess(0x1000, false, processId);
        if (handle == IntPtr.Zero) return true;
        try
        {
            if (!OpenProcessToken(handle, 0x0008 /* TOKEN_QUERY */, out var token)) return true;
            try { return GetTokenInformation(token, 20 /* TokenElevation */, out var elevated, 4, out _) && elevated != 0; }
            finally { CloseHandle(token); }
        }
        finally { CloseHandle(handle); }
    }

    delegate bool EnumWindowsProc(IntPtr window, IntPtr parameter);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr window, int command);
    [DllImport("user32.dll")] static extern int GetWindowTextLength(IntPtr window);
    [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr window, int index);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("kernel32.dll")] static extern IntPtr OpenProcess(int access, bool inherit, int processId);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool QueryFullProcessImageName(IntPtr process, int flags, StringBuilder name, ref int size);
    [DllImport("advapi32.dll", SetLastError = true)] static extern bool OpenProcessToken(IntPtr process, int access, out IntPtr token);
    [DllImport("advapi32.dll", SetLastError = true)] static extern bool GetTokenInformation(IntPtr token, int infoClass, out int information, int length, out int returned);
}
