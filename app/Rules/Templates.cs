using WinSwitch.Power;
using WinSwitch.Settings;
namespace WinSwitch.Rules;

// Starting profile and the wizard's profiles (SPEC.md › First run)
public static class Templates
{
    public static readonly string[] Colours = { "#2F7FD8", "#3FAE68", "#E0812F", "#8E6AD8", "#D2475A", "#2BA6A6", "#9AA1AB" };

    // Everything as it was before WinSwitch, every setting filled in
    public static Profile Starting(Snapshot snapshot) => new() { Name = "Starting profile", Colour = "#9AA1AB", IsStarting = true, Values = snapshot.Values.ToDictionary(entry => entry.Key, entry => entry.Value.Clone()) };

    public static void Set(Profile profile, string key, int? pluggedIn, int? onBattery)
    {
        var setting = Catalog.Find(key); if (setting == null) return;
        profile.Values[key] = setting.TwoSided ? new SidePair { Ac = pluggedIn, Dc = onBattery } : new SidePair { Ac = pluggedIn };
    }

    // The Battery or speed dial: sets every row it covers for one side
    public static void ApplySpeed(Profile profile, bool pluggedIn, int? position)
    {
        if (pluggedIn) profile.SpeedAc = position; else profile.SpeedDc = position;
        if (position == null) return;
        foreach (var (key, values) in Catalog.SpeedPresets)
        {
            if (!profile.Values.TryGetValue(key, out var pair)) profile.Values[key] = pair = new SidePair();
            if (pluggedIn) pair.Ac = values[position.Value]; else pair.Dc = values[position.Value];
        }
    }
    // Custom: a row under the dial no longer matches the dial's position
    public static bool IsCustom(Profile profile, bool pluggedIn, ISet<string> notSupported)
    {
        var position = pluggedIn ? profile.SpeedAc : profile.SpeedDc;
        if (position == null) return false;
        return Catalog.SpeedPresets.Any(preset => !notSupported.Contains(preset.Key) && profile.Get(preset.Key) is { } pair && (pluggedIn ? pair.Ac : pair.Dc) != preset.Value[position.Value]);
    }

    public static Profile Desk(int screenMinutes)
    {
        var profile = new Profile { Name = "Desk", Colour = Colours[0] };
        Set(profile, "screenOff", screenMinutes * 60, 600); Set(profile, "sleepAfter", 0, 900); Set(profile, "lid", 0, 1);
        ApplySpeed(profile, true, 4); ApplySpeed(profile, false, 2);
        return profile;
    }
    public static Profile OutAndAbout(int batteryPriority, int lidAway, int lidAwayPlugged = 1)
    {
        var profile = new Profile { Name = "Out and about", Colour = Colours[1] };
        Set(profile, "screenOff", 600, 300); Set(profile, "sleepAfter", 1800, 600); Set(profile, "lid", lidAwayPlugged, lidAway == 3 ? 3 : lidAway == 2 ? 2 : 1); // lid values: 0 do nothing, 1 sleep, 2 hibernate, 3 shut down
        if (lidAway == 0) Set(profile, "hibernateAfter", 0, 3600); // sleep, then hibernate after an hour asleep
        Set(profile, "standbyNetwork", null, 0); // no network while asleep in the bag
        ApplySpeed(profile, true, 2); ApplySpeed(profile, false, batteryPriority);
        return profile;
    }
    public static Profile Presenting()
    {
        var profile = new Profile { Name = "Presenting", Colour = Colours[2] };
        Set(profile, "screenOff", 0, 0); Set(profile, "dimAfter", 0, 0); Set(profile, "sleepAfter", 0, 0); Set(profile, "lockedScreenOff", 1800, 1800);
        ApplySpeed(profile, true, 3); ApplySpeed(profile, false, 2);
        return profile;
    }
    public static Profile Server(int screenMinutes)
    {
        var profile = new Profile { Name = "Always on", Colour = Colours[3] };
        Set(profile, "screenOff", Math.Max(1, screenMinutes / 6) * 60, 300); Set(profile, "sleepAfter", 0, 0); Set(profile, "hibernateAfter", 0, 0); Set(profile, "lid", 0, 0); Set(profile, "wakeTimers", 1, 1);
        ApplySpeed(profile, true, 2); ApplySpeed(profile, false, 2);
        return profile;
    }
    public static Tweak Night(int screenMinutes, string from, string to, int? volumeChange)
    {
        var tweak = new Tweak { Name = "Night", Colour = "#5B5FC7", From = from, To = to, VolumeChange = volumeChange };
        Set(tweak, "screenOff", screenMinutes * 60, screenMinutes * 60);
        return tweak;
    }
}
