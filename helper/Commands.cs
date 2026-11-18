using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.ServiceProcess;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;
using Microsoft.Win32;
namespace WinSwitchHelper;

// The fixed list of things the helper does. It never takes a command line or a program to run: only names it checks against what is on this PC.
static class Commands
{
    public static string Version => typeof(Commands).Assembly.GetName().Version.ToString(3);
    static readonly HashSet<string> untouchableProcesses = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "csrss", "wininit", "winlogon", "lsass", "smss", "services", "System", "Idle", "Registry", "Secure System", "MemCompression", "svchost", "dwm", "fontdrvhost", "sihost", "explorer", "WinSwitchHelper", "MsMpEng", "LsaIso" };
    static readonly HashSet<string> protectedServices = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Program.ServiceName, "RpcSs", "RpcEptMapper", "DcomLaunch", "LSM", "EventLog", "Winmgmt", "WinDefend", "mpssvc", "BFE", "SamSs", "Schedule", "ProfSvc", "Power", "gpsvc", "CryptSvc", "Dhcp", "Dnscache", "nsi", "PlugPlay", "SecurityHealthService", "Sense", "WdNisSvc", "wscsvc", "TrustedInstaller", "AppIDSvc", "KeyIso", "VaultSvc", "UserManager", "CoreMessagingRegistrar", "BrokerInfrastructure", "SystemEventsBroker", "StateRepository", "TimeBrokerSvc", "AudioSrv", "AudioEndpointBuilder", "EFS", "Netlogon", "LanmanWorkstation" };
    const string FastStartupKey = @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Session Manager\Power", HibernateKey = @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Power";
    static string OriginalPath => Path.Combine(Program.DataFolder, "helper-original.json");

    static readonly HashSet<string> adminOnly = new HashSet<string> { "service", "end-process", "run-task", "firewall", "uninstall", "hibernate", "fast-startup", "sign-in-on-wake" };

    public static object Do(string command, Dictionary<string, object> request, bool callerIsAdmin)
    {
        if (adminOnly.Contains(command) && !callerIsAdmin) throw new Exception("Only an administrator account can do that");
        string Text(string key) => request.TryGetValue(key, out var value) && value is string text ? text : throw new Exception($"Missing {key}");
        bool On(string key = "on") => request.TryGetValue(key, out var value) && value is bool flag ? flag : throw new Exception($"Missing {key}");
        int Number(string key) => request.TryGetValue(key, out var value) && value is int number ? number : throw new Exception($"Missing {key}");
        switch (command)
        {
            case "version": return Version;
            case "state": return State();
            case "device": SetDevice(Text("id"), request.ContainsKey("allowOff") ? On("allowOff") : (bool?)null, request.ContainsKey("allowWake") ? On("allowWake") : (bool?)null); return true;
            case "wake-devices": return SetAll("MSPower_DeviceWakeEnable", Devices().Where(device => device.Kind == Text("kind") && device.AllowWake != null), On());
            case "power-off-devices": return SetAll("MSPower_DeviceEnable", Devices().Where(device => device.Kind == Text("kind") && device.AllowOff != null), On());
            case "wol": SetWakeOnLan(On()); return true;
            case "hibernate": var hibernate = Program.Run("powercfg.exe", On() ? "/hibernate on" : "/hibernate off"); if (hibernate.exitCode != 0) throw new Exception(hibernate.output.Trim()); return true;
            case "fast-startup": Registry.SetValue(FastStartupKey, "HiberbootEnabled", On() ? 1 : 0, RegistryValueKind.DWord); return true;
            case "service": return Service(Text("name"), On("start"));
            case "end-process": return EndProcess(Number("processId"));
            case "run-task": return RunTask(Text("path"));
            case "power": return Power(Text("action"));
            case "firewall": return Firewall(Number("port"), request.TryGetValue("program", out var program) ? program as string : null, On());
            case "sign-in-on-wake": return SignInOnWake(Text("plan"), Number("ac"), Number("dc"));
            case "restore": Restore(); return true;
            case "uninstall": Restore(); System.Threading.Tasks.Task.Delay(500).ContinueWith(_ => Program.ScheduleRemoval()); return true;
            default: throw new Exception($"Unknown command {command}");
        }
    }

    // ---- Devices: "Allow the computer to turn off this device" and "Allow this device to wake the computer", as Device Manager sets them
    public sealed class Device { public string Id, Name, Kind; public bool? AllowOff, AllowWake; }

    public static List<Device> Devices()
    {
        var names = new Dictionary<string, (string name, string kind)>(StringComparer.OrdinalIgnoreCase);
        using (var searcher = new ManagementObjectSearcher("SELECT DeviceID, Name, PNPClass FROM Win32_PnPEntity"))
            foreach (ManagementObject entity in searcher.Get()) { var name = entity["Name"] as string ?? ""; names[entity["DeviceID"] as string ?? ""] = (name, Kind(name, entity["PNPClass"] as string ?? "")); }
        var off = ReadFlags("MSPower_DeviceEnable"); var wake = ReadFlags("MSPower_DeviceWakeEnable");
        return off.Keys.Union(wake.Keys, StringComparer.OrdinalIgnoreCase).Select(id =>
        {
            names.TryGetValue(Regex.Replace(id, @"_\d+$", ""), out var found);
            return new Device { Id = id, Name = found.name ?? id, Kind = found.kind ?? "other", AllowOff = off.TryGetValue(id, out var allowOff) ? allowOff : (bool?)null, AllowWake = wake.TryGetValue(id, out var allowWake) ? allowWake : (bool?)null };
        }).OrderBy(device => device.Kind).ThenBy(device => device.Name).ToList();
    }

    static string Kind(string name, string pnpClass)
    {
        if (pnpClass == "Net") return Regex.IsMatch(name, "virtual|hyper-v|vpn|tap-|wan miniport|loopback|bluetooth", RegexOptions.IgnoreCase) ? "other" : Regex.IsMatch(name, @"wireless|wi-?fi|wlan|802\.11", RegexOptions.IgnoreCase) ? "wifi" : "ethernet";
        if (pnpClass == "Bluetooth") return "bluetooth";
        if (pnpClass == "USB" && name.IndexOf("hub", StringComparison.OrdinalIgnoreCase) >= 0) return "usbhub";
        if (pnpClass == "Mouse" || pnpClass == "Keyboard") return "input";
        return "other";
    }

    static Dictionary<string, bool> ReadFlags(string wmiClass)
    {
        var flags = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        try { using var searcher = new ManagementObjectSearcher(new ManagementScope(@"root\WMI"), new ObjectQuery($"SELECT InstanceName, Enable FROM {wmiClass}")); foreach (ManagementObject flag in searcher.Get()) flags[flag["InstanceName"] as string ?? ""] = flag["Enable"] is bool enabled && enabled; }
        catch (Exception error) { Log.Write($"{wmiClass}: {error.Message}"); }
        return flags;
    }

    static void SetFlag(string wmiClass, string id, bool on)
    {
        if (!ReadFlags(wmiClass).TryGetValue(id, out var now)) throw new Exception("No such device");
        if (now == on) return;
        using var searcher = new ManagementObjectSearcher(new ManagementScope(@"root\WMI"), new ObjectQuery($"SELECT * FROM {wmiClass} WHERE InstanceName = '{id.Replace("\\", "\\\\").Replace("'", "\\'")}'"));
        foreach (ManagementObject flag in searcher.Get()) { flag["Enable"] = on; flag.Put(); }
        Log.Write($"{wmiClass} {id} = {on}");
    }

    // Some devices (often a laptop's built-in keyboard) refuse the change; the rest still get it. Answers how many did.
    static int SetAll(string wmiClass, IEnumerable<Device> devices, bool on)
    {
        var changed = 0;
        foreach (var device in devices) { try { SetFlag(wmiClass, device.Id, on); changed++; } catch (Exception error) { Log.Write($"{wmiClass} {device.Name}: {error.Message}"); } }
        return changed;
    }

    static void SetDevice(string id, bool? allowOff, bool? allowWake)
    {
        if (allowOff != null) SetFlag("MSPower_DeviceEnable", id, allowOff.Value);
        if (allowWake != null) SetFlag("MSPower_DeviceWakeEnable", id, allowWake.Value);
    }

    // ---- Wake on LAN: the wired adapters' "Wake on Magic Packet", plus letting them wake the PC
    static Dictionary<string, string> WakeOnLanAdapters()
    {
        var adapters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var searcher = new ManagementObjectSearcher(new ManagementScope(@"root\StandardCimv2"), new ObjectQuery("SELECT Name, RegistryValue FROM MSFT_NetAdapterAdvancedPropertySettingData WHERE RegistryKeyword = '*WakeOnMagicPacket'"));
            foreach (ManagementObject property in searcher.Get()) adapters[property["Name"] as string ?? ""] = (property["RegistryValue"] as string[])?.FirstOrDefault() ?? "";
        }
        catch (Exception error) { Log.Write($"Wake on LAN: {error.Message}"); }
        return adapters;
    }

    static void SetMagicPacket(string adapter, string value)
    {
        if (!WakeOnLanAdapters().TryGetValue(adapter, out var now) || now == value || !Regex.IsMatch(value, @"^\d$")) return;
        var result = Program.Run("powershell.exe", $"-NoProfile -NonInteractive -Command \"Set-NetAdapterAdvancedProperty -Name '{adapter.Replace("'", "''").Replace("\"", "")}' -RegistryKeyword '*WakeOnMagicPacket' -RegistryValue {value}\"");
        Log.Write($"Wake on LAN {adapter} = {value}{(result.exitCode == 0 ? "" : " failed: " + result.output.Trim())}");
    }

    static void SetWakeOnLan(bool on)
    {
        var wired = Devices().Where(device => device.Kind == "ethernet").ToList();
        foreach (var adapter in WakeOnLanAdapters().Keys) SetMagicPacket(adapter, on ? "1" : "0");
        if (on) foreach (var device in wired.Where(device => device.AllowWake == false)) SetFlag("MSPower_DeviceWakeEnable", device.Id, true);
    }

    // ---- Hibernate and fast startup
    static bool Hibernate => Registry.GetValue(HibernateKey, "HibernateEnabled", 1) is int value ? value != 0 : true;
    static bool FastStartup => Registry.GetValue(FastStartupKey, "HiberbootEnabled", 1) is int value ? value != 0 : true;

    static Dictionary<string, object> State() => new Dictionary<string, object>
    {
        ["version"] = Version,
        ["devices"] = Devices().Select(device => new Dictionary<string, object> { ["id"] = device.Id, ["name"] = device.Name, ["kind"] = device.Kind, ["allowOff"] = device.AllowOff, ["allowWake"] = device.AllowWake }).ToList(),
        ["wakeOnLan"] = WakeOnLanAdapters().Select(adapter => new Dictionary<string, object> { ["adapter"] = adapter.Key, ["on"] = adapter.Value == "1" }).ToList(),
        ["hibernate"] = Hibernate,
        ["fastStartup"] = FastStartup,
    };

    // ---- Original settings: captured the first time the helper starts, put back on restore and uninstall
    public static void CaptureOriginalOnce()
    {
        try
        {
            if (File.Exists(OriginalPath)) return;
            var devices = Devices();
            var original = new Dictionary<string, object>
            {
                ["capturedAt"] = DateTime.Now.ToString("s"),
                ["allowOff"] = devices.Where(device => device.AllowOff != null).ToDictionary(device => device.Id, device => (object)device.AllowOff.Value),
                ["allowWake"] = devices.Where(device => device.AllowWake != null).ToDictionary(device => device.Id, device => (object)device.AllowWake.Value),
                ["wakeOnLan"] = WakeOnLanAdapters().ToDictionary(adapter => adapter.Key, adapter => (object)adapter.Value),
                ["hibernate"] = Hibernate, ["fastStartup"] = FastStartup,
            };
            Directory.CreateDirectory(Program.DataFolder);
            File.WriteAllText(OriginalPath, new JavaScriptSerializer().Serialize(original));
            Log.Write("Captured original device power, Wake on LAN, hibernate and fast startup");
        }
        catch (Exception error) { Log.Write($"Capture failed: {error.Message}"); }
    }

    public static void Restore()
    {
        if (!File.Exists(OriginalPath)) return;
        var original = new JavaScriptSerializer().DeserializeObject(File.ReadAllText(OriginalPath)) as Dictionary<string, object>;
        if (original == null) return;
        void Each(string key, Action<string, object> apply) { if (original.TryGetValue(key, out var group) && group is Dictionary<string, object> values) foreach (var pair in values) { try { apply(pair.Key, pair.Value); } catch (Exception error) { Log.Write($"Restore {key} {pair.Key}: {error.Message}"); } } }
        Each("allowOff", (id, value) => SetFlag("MSPower_DeviceEnable", id, (bool)value));
        Each("allowWake", (id, value) => SetFlag("MSPower_DeviceWakeEnable", id, (bool)value));
        Each("wakeOnLan", (adapter, value) => SetMagicPacket(adapter, value as string ?? ""));
        try { if (original.TryGetValue("hibernate", out var hibernate) && (bool)hibernate != Hibernate) Program.Run("powercfg.exe", (bool)hibernate ? "/hibernate on" : "/hibernate off"); } catch { }
        try { if (original.TryGetValue("fastStartup", out var fast) && (bool)fast != FastStartup) Registry.SetValue(FastStartupKey, "HiberbootEnabled", (bool)fast ? 1 : 0, RegistryValueKind.DWord); } catch { }
        Log.Write("Restored original device settings");
    }

    // ---- Apps to stop: services, processes the user can't end, scheduled tasks
    static bool Service(string name, bool start)
    {
        if (!Regex.IsMatch(name, @"^[\w.\- ]{1,256}$")) throw new Exception("Bad service name");
        using var service = new ServiceController(name);
        _ = service.Status; // throws when there's no such service
        if (protectedServices.Contains(service.ServiceName) || (service.ServiceType & (ServiceType.KernelDriver | ServiceType.FileSystemDriver)) != 0) throw new Exception("WinSwitch doesn't stop that service");
        if (start) { if (service.Status != ServiceControllerStatus.Running) { service.Start(); service.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(30)); } }
        else if (service.Status != ServiceControllerStatus.Stopped) { service.Stop(); service.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(30)); }
        Log.Write($"Service {name} {(start ? "started" : "stopped")}");
        return true;
    }

    static bool EndProcess(int processId)
    {
        using var process = Process.GetProcessById(processId);
        if (process.SessionId == 0 || untouchableProcesses.Contains(process.ProcessName)) throw new Exception("WinSwitch doesn't end that process"); // services go through "service"
        var result = Program.Run("taskkill.exe", $"/PID {processId} /T /F");
        Log.Write($"Ended {process.ProcessName} ({processId}){(result.exitCode == 0 ? "" : ": " + result.output.Trim())}");
        return result.exitCode == 0;
    }

    static bool RunTask(string path)
    {
        if (!Regex.IsMatch(path, @"^\\[^""\r\n\0%]{1,400}$")) throw new Exception("Bad task path");
        if (Program.Run("schtasks.exe", $"/query /tn \"{path}\"").exitCode != 0) throw new Exception("No such task");
        var result = Program.Run("schtasks.exe", $"/run /tn \"{path}\"");
        Log.Write($"Ran task {path}{(result.exitCode == 0 ? "" : ": " + result.output.Trim())}");
        return result.exitCode == 0;
    }

    // ---- Power actions (for remote control with nobody signed in)
    [DllImport("powrprof.dll")] static extern bool SetSuspendState(bool hibernate, bool forceCritical, bool disableWakeEvent);

    static bool Power(string action)
    {
        Log.Write($"Power: {action}");
        switch (action)
        {
            case "sleep": return SetSuspendState(false, false, false);
            case "hibernate": return SetSuspendState(true, false, false);
            case "restart": return Program.Run("shutdown.exe", "/r /t 0 /d p:0:0").exitCode == 0;
            case "shutdown": return Program.Run("shutdown.exe", "/s /t 0 /d p:0:0").exitCode == 0;
            default: throw new Exception("Unknown power action");
        }
    }

    // ---- The phone page's firewall rule: private networks, one port, WinSwitch's own exe only
    static bool Firewall(int port, string program, bool on)
    {
        if (port < 1024 || port > 65535) throw new Exception("Bad port");
        Program.Run("netsh.exe", "advfirewall firewall delete rule name=\"WinSwitch phone page\"");
        if (!on) return true;
        if (program == null || program.IndexOf('"') >= 0 || !program.EndsWith(@"\WinSwitch.exe", StringComparison.OrdinalIgnoreCase) || !File.Exists(program)) throw new Exception("Bad program");
        return Program.Run("netsh.exe", $"advfirewall firewall add rule name=\"WinSwitch phone page\" dir=in action=allow protocol=TCP localport={port} profile=private program=\"{program}\"").exitCode == 0;
    }

    // Sign in after waking (CONSOLELOCK) for one plan; Windows lets only an administrator change it. -1 = leave that side
    static bool SignInOnWake(string plan, int ac, int dc)
    {
        if (!Guid.TryParse(plan, out var scheme)) throw new Exception("Not a power plan");
        if (ac is 0 or 1) { var result = Program.Run("powercfg.exe", $"/setacvalueindex {scheme} SUB_NONE CONSOLELOCK {ac}"); if (result.exitCode != 0) throw new Exception(result.output.Trim()); }
        if (dc is 0 or 1) { var result = Program.Run("powercfg.exe", $"/setdcvalueindex {scheme} SUB_NONE CONSOLELOCK {dc}"); if (result.exitCode != 0) throw new Exception(result.output.Trim()); }
        return true;
    }
}

