using System.IO;
using System.Text.Json;
namespace WinSwitch.Settings;

// A value for plugged in and on battery; one-sided settings use Ac only. null = leave as it is
public class SidePair
{
    public int? Ac { get; set; }
    public int? Dc { get; set; }
    public SidePair Clone() => new() { Ac = Ac, Dc = Dc };
}

// A named set of settings (SPEC.md › Profiles). Only the keys present are changed; the rest are left as they are
public class Profile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string Name { get; set; } = "";
    public string Colour { get; set; } = "#2F7FD8";
    public bool IsStarting { get; set; } // the import of how the PC was (SPEC.md › First run)
    public Dictionary<string, SidePair> Values { get; set; } = new();
    public int? SpeedAc { get; set; } // Battery or speed dial position (0..4), null = not set
    public int? SpeedDc { get; set; }
    public SidePair? Get(string key) => Values.TryGetValue(key, out var pair) && (pair.Ac != null || pair.Dc != null) ? pair : null;
}

// A few settings laid over whichever profile is on, e.g. Night (SPEC.md › Tweaks)
public class Tweak : Profile
{
    public bool Scheduled { get; set; } = true;
    public string From { get; set; } = "22:00";
    public string To { get; set; } = "07:00";
    public int? VolumeChange { get; set; } // up or down by this many %, put back when the tweak ends
}

// "When this is connected, use that profile" (SPEC.md › Situations)
public class Situation
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string Kind { get; set; } = "dock"; // dock | monitor | device | network | battery
    public string Name { get; set; } = "";
    public Dictionary<string, string> Things { get; set; } = new(); // thing -> readable name
    public int BatteryBelow { get; set; } = 20;
    public string ProfileId { get; set; } = "";
}

public class GeneralSettings
{
    public bool AutoUpdate { get; set; } = true;
    public string UpdateChannel { get; set; } = "stable"; // stable | dev
    public bool StartWithWindows { get; set; } = true;
    public string Notifications { get; set; } = "all"; // all | questions | off
    public string LastRunVersion { get; set; } = "";
    public bool WizardDone { get; set; }
    public List<string> IgnoredThings { get; set; } = new(); // docks and monitors you said to ignore
}

public class TweakOverride { public bool On { get; set; } public bool ScheduledWhenSet { get; set; } }

// WinSwitch's own power plan and what it last wrote (SPEC.md › Profiles, Playing nicely with Windows)
public class PowerState
{
    public string OwnPlan { get; set; } = ""; // the copy WinSwitch writes to
    public bool WritesToOriginal { get; set; } // Windows refused a copy, so WinSwitch writes to the original plan (restore writes its values back)
    public bool Paused { get; set; } // after Restore everything, or when you picked another plan in Windows
    public string HeldProfileId { get; set; } = ""; // picked by hand from the tray; holds until the situation changes
    public string HeldSituation { get; set; } = "";
    public Dictionary<string, TweakOverride> TweakOverrides { get; set; } = new(); // tweak id -> on/off by hand, until its next scheduled change
    public Dictionary<string, SidePair> LastWritten { get; set; } = new(); // plan values WinSwitch wrote, to notice changes made in Windows
}

public class AppSettings
{
    public GeneralSettings General { get; set; } = new();
    public List<Profile> Profiles { get; set; } = new();
    public List<Tweak> Tweaks { get; set; } = new();
    public List<Situation> Situations { get; set; } = new();
    public string DefaultProfileId { get; set; } = ""; // Anything else
    public PowerState Power { get; set; } = new();

    static readonly JsonSerializerOptions jsonOptions = new() { WriteIndented = true };
    public Profile? ProfileById(string id) => Profiles.FirstOrDefault(profile => profile.Id == id);

    public static AppSettings Load()
    {
        try { return File.Exists(AppPaths.SettingsFile) ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(AppPaths.SettingsFile), jsonOptions) ?? new() : new(); }
        catch { return new(); }
    }

    public void Save()
    {
        Directory.CreateDirectory(AppPaths.DataFolder);
        var tempFile = AppPaths.SettingsFile + ".tmp";
        File.WriteAllText(tempFile, JsonSerializer.Serialize(this, jsonOptions));
        File.Move(tempFile, AppPaths.SettingsFile, true);
    }
}
