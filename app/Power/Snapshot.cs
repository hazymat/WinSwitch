using System.IO;
using System.Text.Json;
using Microsoft.Win32;
using Windows.Devices.Radios;
using WinSwitch.Settings;
namespace WinSwitch.Power;

// How the PC was before WinSwitch changed anything, taken once on first run (SPEC.md › Original settings)
public class Snapshot
{
    public DateTime TakenAt { get; set; } = DateTime.Now;
    public string Plan { get; set; } = "";
    public string PlanName { get; set; } = "";
    public Dictionary<string, SidePair> Values { get; set; } = new();
    public List<Direct.DevicePower> Devices { get; set; } = new(); // device power saving and wake flags (read only for now)
    public string SoundOutput { get; set; } = "";
    public List<SnapshotSection> Sections { get; set; } = new(); // everything read, as shown on the capture screen (settings and, for reference, apps, tasks and services)

    static readonly JsonSerializerOptions jsonOptions = new() { WriteIndented = true };
    public static Snapshot? Load() { try { return File.Exists(AppPaths.SnapshotFile) ? JsonSerializer.Deserialize<Snapshot>(File.ReadAllText(AppPaths.SnapshotFile), jsonOptions) : null; } catch { return null; } }
    public void Save() { Directory.CreateDirectory(AppPaths.DataFolder); File.WriteAllText(AppPaths.SnapshotFile, JsonSerializer.Serialize(this, jsonOptions)); }

    // The capture screen's sections, in the order they're read
    public static readonly string[] SectionTitles = { "Windows power plan", "Screen, power mode and sound", "Device driver power", "Displays and devices", "Running apps", "Startup apps", "Task Scheduler", "Services" };

    // Reads everything; `report` gets each finding as it's read (section, group, line) for the capture screen
    public static async Task<Snapshot> TakeAsync(Action<string, string, string>? report = null)
    {
        var sections = SectionTitles.ToDictionary(title => title, title => new SnapshotSection { Title = title });
        void Report(string section, string group, string line) { sections[section].Lines.Add(new SnapshotLine { Group = group, Text = line }); report?.Invoke(section, group, line); }
        var plan = PowerApi.ActiveScheme() ?? Guid.Empty;
        var snapshot = new Snapshot { Plan = plan.ToString(), PlanName = PowerApi.SchemeName(plan) };
        Report(SectionTitles[0], "", $"Active power plan: {snapshot.PlanName} {{{plan}}}");
        foreach (var other in PowerApi.Schemes().Where(scheme => scheme != plan)) Report(SectionTitles[0], "", $"Also on this PC: {PowerApi.SchemeName(other)} {{{other}}}");
        snapshot.Values = await Reader.ReadAllAsync(plan);
        snapshot.Devices = Direct.DevicePowerFlags();
        snapshot.SoundOutput = Direct.OutputName();
        // Power mode has a plugged-in and an on-battery choice, kept by Windows in the registry (read only)
        try
        {
            using var overlays = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Power\User\PowerSchemes");
            int? ModeOf(string name) => overlays?.GetValue(name) is string text && Guid.TryParse(text, out var mode) ? mode == PowerApi.ModeEfficiency ? 0 : mode == PowerApi.ModePerformance ? 2 : 1 : null;
            var existing = snapshot.Values.GetValueOrDefault("powerMode");
            var acMode = ModeOf("ActiveOverlayAcPowerScheme"); var dcMode = ModeOf("ActiveOverlayDcPowerScheme");
            if (acMode != null || dcMode != null) snapshot.Values["powerMode"] = new SidePair { Ac = acMode ?? existing?.Ac, Dc = dcMode ?? existing?.Dc };
        }
        catch { }
        // Settings, both sides where Windows keeps two
        foreach (var setting in Catalog.All.Where(setting => snapshot.Values.ContainsKey(setting.Key)).OrderBy(setting => setting.Kind == SettingKind.Plan ? 0 : 1))
        {
            var pair = snapshot.Values[setting.Key];
            string Value(int? value) => value == null ? "?" : setting.Kind == SettingKind.Refresh ? $"{value} Hz" : setting.Describe(value.Value);
            var text = setting.TwoSided ? $"{setting.Label}: {Value(pair.Ac)} plugged in, {Value(pair.Dc)} on battery" : $"{setting.Label}: {Value(pair.Ac)}";
            Report(setting.Kind == SettingKind.Plan ? SectionTitles[0] : SectionTitles[1], setting.Dial, text);
        }
        if (snapshot.SoundOutput != "") Report(SectionTitles[1], Catalog.Sound, "Sound output: " + snapshot.SoundOutput);
        foreach (var device in snapshot.Devices) Report(SectionTitles[2], device.Name, $"May be turned off to save power: {YesNo(device.MayTurnOff)}; may wake the PC: {YesNo(device.MayWake)}");
        try { foreach (var monitor in Shell.MonitorSetup.Current()) Report(SectionTitles[3], "Displays", $"{monitor.Name}{(monitor.Internal ? " (built in)" : "")}"); } catch { }
        try { foreach (var (thing, name) in Rules.Inventory.Take().Things.Where(thing => !thing.Key.StartsWith("monitor:")).OrderBy(thing => thing.Value)) Report(SectionTitles[3], thing.StartsWith("net:") ? "Network adapters" : "Plugged-in devices", name); } catch { }
        // For reference, and so anything WinSwitch stops can be started again the same way
        var windowed = Apps.AppCatalog.WindowedProcessIds();
        foreach (var (path, processes) in Apps.AppCatalog.RunningByPath().OrderBy(entry => Path.GetFileName(entry.Key))) Report(SectionTitles[4], processes.Any(process => windowed.Contains(process.ProcessId)) ? "With a window" : "In the background", $"{Path.GetFileName(path)} ({processes.Count}): {path}");
        var starts = Apps.AppCatalog.StartsWithWindows();
        foreach (var entry in starts.Where(entry => entry.How is "run" or "shortcut")) Report(SectionTitles[5], entry.How == "run" ? "Run key" : "Startup folder", $"{entry.Name}: {entry.CommandLine}");
        foreach (var entry in starts.Where(entry => entry.How == "task")) Report(SectionTitles[6], "Starts at sign-in or boot", $"{entry.Target}: {entry.CommandLine}");
        try
        {
            using var searcher = new System.Management.ManagementObjectSearcher("SELECT Name, DisplayName, StartMode, State FROM Win32_Service");
            foreach (System.Management.ManagementObject service in searcher.Get()) using (service) Report(SectionTitles[7], $"{service["StartMode"]} start", $"{service["DisplayName"]} ({service["Name"]}): {service["State"]}");
        }
        catch { }
        snapshot.Sections = sections.Values.ToList();
        return snapshot;
    }
    static string YesNo(bool? value) => value == null ? "?" : value.Value ? "yes" : "no";
}

public class SnapshotSection { public string Title { get; set; } = ""; public List<SnapshotLine> Lines { get; set; } = new(); }
public class SnapshotLine { public string Group { get; set; } = ""; public string Text { get; set; } = ""; }

// Reads the current value of every setting this PC has
public static class Reader
{
    public static async Task<Dictionary<string, SidePair>> ReadAllAsync(Guid plan)
    {
        var values = new Dictionary<string, SidePair>();
        var pluggedIn = PowerApi.PowerStatus().pluggedIn;
        foreach (var setting in Catalog.All)
        {
            if (setting.Kind == SettingKind.Plan) { var ac = PowerApi.Read(plan, setting.Subgroup, setting.Setting, true); var dc = PowerApi.Read(plan, setting.Subgroup, setting.Setting, false); if (ac != null || dc != null) values[setting.Key] = new SidePair { Ac = (int?)ac, Dc = (int?)dc }; continue; }
            int? now = setting.Kind switch { SettingKind.Brightness => Direct.Brightness(), SettingKind.PowerMode => Direct.PowerMode(), SettingKind.Refresh => Direct.RefreshRates().Count > 1 ? Direct.RefreshRate() : null, SettingKind.Volume => Direct.Volume(), SettingKind.WiFi => await Direct.RadioState(RadioKind.WiFi), SettingKind.Bluetooth => await Direct.RadioState(RadioKind.Bluetooth), SettingKind.MobileBroadband => await Direct.RadioState(RadioKind.MobileBroadband), _ => null };
            if (now == null) continue;
            // Outside the plan only the current side can be read; the other side starts the same
            values[setting.Key] = setting.TwoSided ? new SidePair { Ac = now, Dc = now } : new SidePair { Ac = now };
        }
        return values;
    }
    public static uint? ReadPlan(Guid plan, SettingDef setting, bool pluggedIn) => PowerApi.Read(plan, setting.Subgroup, setting.Setting, pluggedIn);
}

// Writes one setting through Windows, then reads it back: a value that doesn't stick counts as not supported (SPEC.md › Playing nicely with Windows)
public static class Writer
{
    public static bool WritePlan(Guid plan, SettingDef setting, SidePair pair)
    {
        var ok = true;
        if (pair.Ac is { } ac) ok &= PowerApi.Write(plan, setting.Subgroup, setting.Setting, true, (uint)ac) && PowerApi.Read(plan, setting.Subgroup, setting.Setting, true) == (uint)ac;
        if (pair.Dc is { } dc) ok &= PowerApi.Write(plan, setting.Subgroup, setting.Setting, false, (uint)dc) && PowerApi.Read(plan, setting.Subgroup, setting.Setting, false) == (uint)dc;
        return ok;
    }

    // Settings outside the plan take the value for the power source in use now
    public static async Task<bool> WriteDirect(SettingDef setting, SidePair pair, bool pluggedIn)
    {
        var value = setting.TwoSided ? (pluggedIn ? pair.Ac : pair.Dc) : pair.Ac;
        if (value == null) return true;
        return setting.Kind switch
        {
            SettingKind.Brightness => Direct.Brightness() == value || Direct.SetBrightness(value.Value),
            SettingKind.PowerMode => Direct.PowerMode() == value || Direct.SetPowerMode(value.Value),
            SettingKind.Refresh => Direct.ResolveRefresh(value.Value) is var hertz && (hertz == 0 || Direct.SetRefreshRate(hertz)),
            SettingKind.Volume => Direct.Volume() == value || Direct.SetVolume(value.Value),
            SettingKind.WiFi => await Direct.RadioState(RadioKind.WiFi) == value || await Direct.SetRadio(RadioKind.WiFi, value == 1),
            SettingKind.Bluetooth => await Direct.RadioState(RadioKind.Bluetooth) == value || await Direct.SetRadio(RadioKind.Bluetooth, value == 1),
            SettingKind.MobileBroadband => await Direct.RadioState(RadioKind.MobileBroadband) == value || await Direct.SetRadio(RadioKind.MobileBroadband, value == 1),
            _ => true,
        };
    }
}

// Puts the PC back as it was: one setting, or everything (also run by the uninstaller) (SPEC.md › Original settings)
public static class Restorer
{
    public static void RestoreEverything(Snapshot? snapshot, bool deletePlan)
    {
        if (snapshot == null || !Guid.TryParse(snapshot.Plan, out var originalPlan)) return;
        var settings = AppSettings.Load();
        var pluggedIn = PowerApi.PowerStatus().pluggedIn;
        var planExists = PowerApi.Schemes().Contains(originalPlan);
        foreach (var setting in Catalog.All.Where(setting => snapshot.Values.ContainsKey(setting.Key)))
        {
            var original = snapshot.Values[setting.Key];
            try
            {
                if (setting.Kind == SettingKind.Plan) { if (settings.Power.WritesToOriginal && planExists) Writer.WritePlan(originalPlan, setting, original); }
                else Writer.WriteDirect(setting, original, pluggedIn).Wait(TimeSpan.FromSeconds(5));
            }
            catch { }
        }
        if (planExists) PowerApi.SetActiveScheme(originalPlan);
        if (deletePlan && Guid.TryParse(settings.Power.OwnPlan, out var ownPlan) && ownPlan != originalPlan && PowerApi.Schemes().Contains(ownPlan)) PowerApi.Delete(ownPlan);
    }
}
