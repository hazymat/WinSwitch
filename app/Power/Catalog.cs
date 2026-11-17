namespace WinSwitch.Power;

// How a setting is reached: a power plan value (powrprof), or one of the few things outside the plan
public enum SettingKind { Plan, Brightness, PowerMode, Refresh, WiFi, Bluetooth, Volume }

public record Option(int Value, string Label);

// One setting WinSwitch can set. Plugged in and on battery values for two-sided ones (as Windows stores them), one value for the rest
public record SettingDef(string Key, string Dial, string Label, SettingKind Kind, bool TwoSided, bool OnDialFace, List<Option> Options, Guid Subgroup = default, Guid Setting = default)
{
    public string Describe(int value) => Options.FirstOrDefault(option => option.Value == value)?.Label ?? (Options == Catalog.Time ? Catalog.TimeLabel(value) : value.ToString());
}

// Every setting a profile can set, grouped under the five dials (SPEC.md › Simple and powerful)
public static class Catalog
{
    public const string Screen = "Screen", Sleep = "Sleep and lid", Speed = "Battery or speed", Wireless = "Wireless and devices", Sound = "Sound";
    public static readonly string[] Dials = { Screen, Sleep, Speed, Wireless, Sound };

    static readonly Guid displayGroup = new("7516b95f-f776-4464-8c53-06167f40cc99"), sleepGroup = new("238c9fa8-0aad-41ed-83f4-97be242c8f20"), buttonsGroup = new("4f971e89-eebd-4455-a8de-9e59040e7347"), batteryGroup = new("e73a048d-bf27-4f12-9731-8b2076e8891f"), saverGroup = new("de830923-a562-41af-a086-e3a2c6bad2da"), cpuGroup = new("54533251-82be-4824-96c1-47b60b740d00"), wirelessGroup = new("19cbb8fa-5279-450e-9fac-8a3d5fedd0c1"), usbGroup = new("2a737441-1930-4402-8d77-b2bebba308a3"), pcieGroup = new("501a4d13-42af-4429-9fd1-a8218c268e20"), diskGroup = new("0012ee47-9041-4b5d-9b77-535fba8b1442");

    public static readonly List<Option> Time = new[] { 0, 60, 120, 180, 300, 600, 900, 1200, 1800, 2700, 3600, 7200, 10800, 18000 }.Select(seconds => new Option(seconds, TimeLabel(seconds))).ToList();
    public static string TimeLabel(int seconds) => seconds == 0 ? "Never" : seconds < 60 ? $"{seconds} seconds" : seconds < 3600 ? $"{seconds / 60} minute{(seconds / 60 == 1 ? "" : "s")}" : seconds % 3600 == 0 ? $"{seconds / 3600} hour{(seconds / 3600 == 1 ? "" : "s")}" : $"{seconds / 60} minutes";
    static List<Option> Percent(int step, int from = 0) => Enumerable.Range(0, (100 - from) / step + 1).Select(index => new Option(from + index * step, $"{from + index * step}%")).ToList();
    static List<Option> Choices(params string[] labels) => labels.Select((label, index) => new Option(index, label)).ToList();
    static readonly List<Option> offOn = Choices("Off", "On");

    public static readonly List<SettingDef> All = new()
    {
        // Screen
        new("screenOff", Screen, "Turn off screen after", SettingKind.Plan, true, true, Time, displayGroup, new("3c0bc021-c8a8-4e07-a973-6b14cbcb2b7e")),
        new("brightness", Screen, "Brightness (laptop screen)", SettingKind.Brightness, true, true, Percent(5)),
        new("dimAfter", Screen, "Dim screen after", SettingKind.Plan, true, false, Time, displayGroup, new("17aaa29b-8b43-4b94-aafe-35f64daaf1ee")),
        new("dimmedBrightness", Screen, "Dimmed brightness", SettingKind.Plan, true, false, Percent(5), displayGroup, new("f1fbfde2-a960-4165-9f88-50667911ce96")),
        new("adaptiveBrightness", Screen, "Adaptive brightness", SettingKind.Plan, true, false, offOn, displayGroup, new("fbd9aa66-9553-4097-ba44-ed6e9d65eab8")),
        new("lockedScreenOff", Screen, "Turn off screen when locked after", SettingKind.Plan, true, false, Time.Where(option => option.Value is > 0 and <= 1800).Prepend(new Option(30, "30 seconds")).Prepend(new Option(10, "10 seconds")).ToList(), displayGroup, new("8ec4b3a5-6868-48c2-be75-4f3044be88a7")),
        // Sleep and lid
        new("sleepAfter", Sleep, "Sleep after", SettingKind.Plan, true, true, Time, sleepGroup, new("29f6c1db-86da-48c5-9fdb-f2b67b1f44da")),
        new("lid", Sleep, "When I close the lid", SettingKind.Plan, true, true, Choices("Do nothing", "Sleep", "Hibernate", "Shut down"), buttonsGroup, new("5ca83367-6e45-459f-a27b-476b1d01c936")),
        new("hibernateAfter", Sleep, "Hibernate after", SettingKind.Plan, true, false, Time, sleepGroup, new("9d7815a6-7ee4-497e-8888-515a05f02364")),
        new("powerButton", Sleep, "Power button", SettingKind.Plan, true, false, Choices("Do nothing", "Sleep", "Hibernate", "Shut down", "Turn off the screen"), buttonsGroup, new("7648efa3-dd9c-4e3e-b566-50f929386280")),
        new("standbyNetwork", Sleep, "Network while asleep", SettingKind.Plan, true, false, Choices("Disconnect", "Stay connected", "Let Windows decide"), PowerApi.NoSubgroup, new("f15576e8-98b7-4186-b944-eafa664402d9")),
        new("hybridSleep", Sleep, "Hybrid sleep", SettingKind.Plan, true, false, offOn, sleepGroup, new("94ac6d29-73ce-41a6-809f-6363ba21b47e")),
        new("wakeTimers", Sleep, "Wake timers", SettingKind.Plan, true, false, Choices("Off", "On", "Important ones only"), sleepGroup, new("bd3b718a-0680-4d9d-8ab2-e1d2b4ac806d")),
        new("signInOnWake", Sleep, "Sign in after waking", SettingKind.Plan, true, false, Choices("Not needed", "Every time"), PowerApi.NoSubgroup, new("0e796bdb-100d-47d6-a2d5-f7d2daa51f51")),
        new("lowLevel", Sleep, "Low battery level", SettingKind.Plan, true, false, Percent(1, 0).Where(option => option.Value <= 50).ToList(), batteryGroup, new("8183ba9a-e910-48da-8769-14ae6dc1170a")),
        new("lowAction", Sleep, "Low battery action", SettingKind.Plan, true, false, Choices("Do nothing", "Sleep", "Hibernate", "Shut down"), batteryGroup, new("d8742dcb-3e6a-4b3c-b3fe-374623cdcf06")),
        new("criticalLevel", Sleep, "Critical battery level", SettingKind.Plan, true, false, Percent(1, 0).Where(option => option.Value <= 30).ToList(), batteryGroup, new("9a66d8d7-4ff7-4ef9-b5a2-5a326ca2a469")),
        new("criticalAction", Sleep, "Critical battery action", SettingKind.Plan, true, false, Choices("Do nothing", "Sleep", "Hibernate", "Shut down"), batteryGroup, new("637ea02f-bbcb-4015-8e2c-a1c7b9c0b546")),
        new("reserveLevel", Sleep, "Reserve battery level", SettingKind.Plan, true, false, Percent(1, 0).Where(option => option.Value <= 30).ToList(), batteryGroup, new("f3c5027d-cd16-4930-aa6b-90db844a8f00")),
        new("saverAt", Sleep, "Energy saver turns on at", SettingKind.Plan, true, false, Percent(5), saverGroup, new("e69653ca-cf7f-4f05-aa73-cb833fa90ad4")),
        new("saverBrightness", Sleep, "Energy saver brightness", SettingKind.Plan, true, false, Percent(5), saverGroup, new("13d09884-f74e-474a-a852-b6bde8ad03a8")),
        // Battery or speed (set together by the dial's slider)
        new("powerMode", Speed, "Power mode", SettingKind.PowerMode, true, false, Choices("Best power efficiency", "Balanced", "Best performance")),
        new("cpuMax", Speed, "Maximum processor state", SettingKind.Plan, true, false, Percent(5, 5), cpuGroup, new("bc5038f7-23e0-4960-96da-33abaf5935ec")),
        new("cpuMin", Speed, "Minimum processor state", SettingKind.Plan, true, false, Percent(5, 0), cpuGroup, new("893dee8e-2bef-41e0-89c6-b55d0929964c")),
        new("boost", Speed, "Processor boost", SettingKind.Plan, true, false, Choices("Off", "On", "Aggressive", "Efficient", "Efficient aggressive"), cpuGroup, new("be337238-0d82-4146-a960-4f3749d470c7")),
        new("energyPreference", Speed, "Energy preference (0 = speed)", SettingKind.Plan, true, false, Percent(10), cpuGroup, new("36687f9e-e3a5-4dbf-b1dc-15eb381c6863")),
        new("cooling", Speed, "Cooling", SettingKind.Plan, true, false, Choices("Slow down first", "Fan first"), cpuGroup, new("94d3a615-a899-4ac5-ae2b-e4d8f634367f")),
        new("refresh", Speed, "Laptop screen refresh rate", SettingKind.Refresh, true, false, new()),
        new("wifiSaving", Speed, "Wi-Fi power saving", SettingKind.Plan, true, false, Choices("Off", "Low", "Medium", "Maximum"), wirelessGroup, new("12bbebe6-58d6-4636-95bb-3217ef867c1a")),
        new("usbSuspend", Speed, "USB power saving", SettingKind.Plan, true, false, offOn, usbGroup, new("48e6b7a6-50f5-4782-a5d4-53bb8f07e226")),
        new("pcieSaving", Speed, "PCI Express power saving", SettingKind.Plan, true, false, Choices("Off", "Moderate", "Maximum"), pcieGroup, new("ee12f906-d277-404b-b6da-e5fa1a576df5")),
        new("diskOff", Speed, "Turn off hard disk after", SettingKind.Plan, true, false, Time, diskGroup, new("6738e2c4-e8a5-4a42-b16a-e040e769756e")),
        // Wireless and devices
        new("wifi", Wireless, "Wi-Fi", SettingKind.WiFi, false, true, offOn),
        new("bluetooth", Wireless, "Bluetooth", SettingKind.Bluetooth, false, true, offOn),
        // Sound
        new("volume", Sound, "Volume", SettingKind.Volume, false, true, Percent(5)),
    };
    public static SettingDef? Find(string key) => All.FirstOrDefault(setting => setting.Key == key);

    // The Battery or speed dial: Longest battery (0) to Fastest (4). Each position sets these rows; the dial shows Custom when a row differs
    public static readonly string[] SpeedLabels = { "Longest battery", "Battery", "Balanced", "Faster", "Fastest" };
    public static readonly Dictionary<string, int[]> SpeedPresets = new()
    {
        ["powerMode"] = new[] { 0, 0, 1, 1, 2 },
        ["cpuMax"] = new[] { 60, 80, 100, 100, 100 },
        ["boost"] = new[] { 0, 3, 1, 2, 2 },
        ["energyPreference"] = new[] { 80, 60, 50, 30, 0 },
        ["cooling"] = new[] { 0, 0, 1, 1, 1 },
        ["wifiSaving"] = new[] { 3, 2, 1, 0, 0 },
        ["usbSuspend"] = new[] { 1, 1, 1, 0, 0 },
        ["pcieSaving"] = new[] { 2, 2, 1, 0, 0 },
        ["refresh"] = new[] { -1, -1, -2, -2, -2 }, // -1 = the screen's lowest rate, -2 = its highest (worked out per PC)
    };
}
