using System.Management;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using Windows.Devices.Radios;
using WinSwitch.Rules;
using WinSwitch.Shell;
namespace WinSwitch.Power;

// The settings that live outside the power plan, each through Windows' own documented API (SPEC.md › Playing nicely with Windows)
public static class Direct
{
    // ---- Laptop screen brightness (WMI; only built-in panels answer, so external monitors are never touched) ----
    public static int? Brightness()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(new ManagementScope(@"root\WMI"), new SelectQuery("SELECT CurrentBrightness FROM WmiMonitorBrightness WHERE Active=TRUE"));
            foreach (ManagementObject monitor in searcher.Get()) using (monitor) return Convert.ToInt32(monitor["CurrentBrightness"]);
        }
        catch { }
        return null;
    }
    public static bool SetBrightness(int percent)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(new ManagementScope(@"root\WMI"), new SelectQuery("SELECT * FROM WmiMonitorBrightnessMethods WHERE Active=TRUE"));
            foreach (ManagementObject monitor in searcher.Get()) using (monitor) monitor.InvokeMethod("WmiSetBrightness", new object[] { (uint)1, (byte)Math.Clamp(percent, 0, 100) });
            return Math.Abs((Brightness() ?? -100) - percent) <= 2;
        }
        catch { return false; }
    }

    // ---- Windows 11 power mode, as 0 efficiency / 1 balanced / 2 performance ----
    public static int? PowerMode() => PowerApi.PowerMode() is { } mode ? mode == PowerApi.ModeEfficiency ? 0 : mode == PowerApi.ModePerformance ? 2 : mode == PowerApi.ModeBalanced ? 1 : null : null;
    public static bool SetPowerMode(int mode) => PowerApi.SetPowerMode(mode switch { 0 => PowerApi.ModeEfficiency, 2 => PowerApi.ModePerformance, _ => PowerApi.ModeBalanced });

    // ---- Built-in screen refresh rate (EnumDisplaySettings / ChangeDisplaySettingsEx) ----
    static string? InternalScreen() => MonitorSetup.Current().FirstOrDefault(monitor => monitor.Internal)?.GdiName;
    public static List<int> RefreshRates()
    {
        var screen = InternalScreen(); if (screen == null) return new();
        var current = NewDevMode(); if (!EnumDisplaySettings(screen, -1, ref current)) return new();
        var rates = new SortedSet<int>();
        var mode = NewDevMode();
        for (var index = 0; EnumDisplaySettings(screen, index, ref mode); index++)
            if (mode.dmPelsWidth == current.dmPelsWidth && mode.dmPelsHeight == current.dmPelsHeight && mode.dmBitsPerPel == current.dmBitsPerPel && mode.dmDisplayFrequency > 1) rates.Add(mode.dmDisplayFrequency);
        return rates.ToList();
    }
    public static int? RefreshRate() { var screen = InternalScreen(); var current = NewDevMode(); return screen != null && EnumDisplaySettings(screen, -1, ref current) ? current.dmDisplayFrequency : null; }
    public static bool SetRefreshRate(int hertz)
    {
        var screen = InternalScreen(); if (screen == null) return false;
        var mode = NewDevMode(); if (!EnumDisplaySettings(screen, -1, ref mode)) return false;
        if (mode.dmDisplayFrequency == hertz) return true;
        mode.dmDisplayFrequency = hertz; mode.dmFields = 0x400000; // DM_DISPLAYFREQUENCY
        return ChangeDisplaySettingsEx(screen, ref mode, IntPtr.Zero, 1 /* CDS_UPDATEREGISTRY */, IntPtr.Zero) == 0 && RefreshRate() == hertz;
    }
    // The Battery or speed presets ask for "lowest" (-1) or "highest" (-2)
    public static int ResolveRefresh(int value) { if (value >= 0) return value; var rates = RefreshRates(); return rates.Count == 0 ? 0 : value == -1 ? rates[0] : rates[^1]; }

    // ---- Wi-Fi and Bluetooth radios (Windows.Devices.Radios, the same switches as Quick Settings) ----
    static async Task<Radio?> RadioOf(RadioKind kind)
    {
        try { if (await Radio.RequestAccessAsync() != RadioAccessStatus.Allowed) return null; return (await Radio.GetRadiosAsync()).FirstOrDefault(radio => radio.Kind == kind); }
        catch { return null; }
    }
    public static async Task<int?> RadioState(RadioKind kind) => await RadioOf(kind) is { } radio ? radio.State == Windows.Devices.Radios.RadioState.On ? 1 : 0 : null;
    public static async Task<bool> SetRadio(RadioKind kind, bool on)
    {
        var radio = await RadioOf(kind); if (radio == null) return false;
        try { return await radio.SetStateAsync(on ? Windows.Devices.Radios.RadioState.On : Windows.Devices.Radios.RadioState.Off) == RadioAccessStatus.Allowed; } catch { return false; }
    }

    // ---- Volume of the default output (Core Audio) ----
    static MMDevice? DefaultOutput() { try { return new MMDeviceEnumerator().GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia); } catch { return null; } }
    public static int? Volume() { using var output = DefaultOutput(); return output == null ? null : (int)Math.Round(output.AudioEndpointVolume.MasterVolumeLevelScalar * 100); }
    public static bool SetVolume(int percent) { using var output = DefaultOutput(); if (output == null) return false; output.AudioEndpointVolume.MasterVolumeLevelScalar = Math.Clamp(percent, 0, 100) / 100f; return true; }
    public static bool? Muted() { using var output = DefaultOutput(); return output?.AudioEndpointVolume.Mute; }
    public static bool SetMuted(bool muted) { using var output = DefaultOutput(); if (output == null) return false; output.AudioEndpointVolume.Mute = muted; return true; }
    public static string OutputName() { using var output = DefaultOutput(); return output?.FriendlyName ?? ""; }
    public static string OutputId() { using var output = DefaultOutput(); return output?.ID ?? ""; }

    // ---- Sound output: the plugged-in outputs, and making one the default for everything (the interface every sound switcher uses; checked by reading back) ----
    public static List<(string id, string name)> Outputs()
    {
        try { return new MMDeviceEnumerator().EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active).Select(device => { using (device) return (device.ID, device.FriendlyName); }).OrderBy(device => device.FriendlyName).ToList(); }
        catch { return new(); }
    }

    public static bool SetOutput(string id)
    {
        if (OutputId() == id) return true;
        if (!Outputs().Any(output => output.id == id)) return false; // not plugged in now
        try { var policy = (IPolicyConfig)new PolicyConfigClient(); for (var role = 0; role < 3; role++) policy.SetDefaultEndpoint(id, role); }
        catch { return false; }
        return OutputId() == id;
    }

    [System.Runtime.InteropServices.ComImport, System.Runtime.InteropServices.Guid("870af99c-171d-4f9e-af0d-e63df40c2bc9")] class PolicyConfigClient { }
    [System.Runtime.InteropServices.ComImport, System.Runtime.InteropServices.Guid("f8679f50-850a-41cf-9c72-430f290290c8"), System.Runtime.InteropServices.InterfaceType(System.Runtime.InteropServices.ComInterfaceType.InterfaceIsIUnknown)]
    interface IPolicyConfig
    {
        // Only SetDefaultEndpoint is used; the slots before it keep the vtable in order
        [PreserveSig] int GetMixFormat(); [PreserveSig] int GetDeviceFormat(); [PreserveSig] int ResetDeviceFormat(); [PreserveSig] int SetDeviceFormat(); [PreserveSig] int GetProcessingPeriod(); [PreserveSig] int SetProcessingPeriod(); [PreserveSig] int GetShareMode(); [PreserveSig] int SetShareMode(); [PreserveSig] int GetPropertyValue(); [PreserveSig] int SetPropertyValue();
        [PreserveSig] int SetDefaultEndpoint([System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPWStr)] string deviceId, int role);
        [PreserveSig] int SetEndpointVisibility();
    }

    // ---- Battery health: how much it holds now against when new (the same figures as Windows' battery report) ----
    public static int? BatteryHealth()
    {
        try
        {
            long Sum(string wmiClass, string property) { long total = 0; using var searcher = new ManagementObjectSearcher(new ManagementScope(@"root\WMI"), new SelectQuery($"SELECT {property} FROM {wmiClass}")); foreach (ManagementObject battery in searcher.Get()) using (battery) total += Convert.ToInt64(battery[property]); return total; }
            var design = Sum("BatteryStaticData", "DesignedCapacity"); var full = Sum("BatteryFullChargedCapacity", "FullChargedCapacity");
            return design > 0 && full > 0 ? (int)Math.Clamp(Math.Round(full * 100.0 / design), 1, 100) : null;
        }
        catch { return null; }
    }
    // Five levels, as a traffic light (Mat 17 Nov)
    public static (int level, string word) HealthWord(int percent) => percent >= 90 ? (0, "Excellent") : percent >= 75 ? (1, "Good") : percent >= 60 ? (2, "Medium") : percent >= 40 ? (3, "Poor") : (4, "Depleted");

    // ---- Device power flags (read only until the helper exists; SPEC.md › Architecture) ----
    public record DevicePower(string Name, bool? MayTurnOff, bool? MayWake);
    public static List<DevicePower> DevicePowerFlags()
    {
        var byInstance = new Dictionary<string, (bool? turnOff, bool? wake)>(StringComparer.OrdinalIgnoreCase);
        void Collect(string wmiClass, bool isWake)
        {
            try
            {
                using var searcher = new ManagementObjectSearcher(new ManagementScope(@"root\WMI"), new SelectQuery($"SELECT InstanceName, Enable FROM {wmiClass}"));
                foreach (ManagementObject device in searcher.Get()) using (device)
                {
                    var instance = System.Text.RegularExpressions.Regex.Replace((string)device["InstanceName"], @"_\d+$", "");
                    byInstance.TryGetValue(instance, out var flags);
                    if (isWake) flags.wake = (bool)device["Enable"]; else flags.turnOff = (bool)device["Enable"];
                    byInstance[instance] = flags;
                }
            }
            catch { }
        }
        Collect("MSPower_DeviceEnable", false); Collect("MSPower_DeviceWakeEnable", true);
        var names = Inventory.DeviceNames();
        return byInstance.Select(entry => new DevicePower(names.TryGetValue(entry.Key, out var name) ? name : entry.Key, entry.Value.turnOff, entry.Value.wake)).OrderBy(device => device.Name).ToList();
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct DevMode
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public short dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra; public int dmFields;
        public int dmPositionX, dmPositionY, dmDisplayOrientation, dmDisplayFixedOutput;
        public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public short dmLogPixels; public int dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency, dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
    }
    static DevMode NewDevMode() => new() { dmDeviceName = "", dmFormName = "", dmSize = (short)Marshal.SizeOf<DevMode>() };
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool EnumDisplaySettings(string deviceName, int modeNumber, ref DevMode devMode);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int ChangeDisplaySettingsEx(string deviceName, ref DevMode devMode, IntPtr window, int flags, IntPtr parameter);
}
