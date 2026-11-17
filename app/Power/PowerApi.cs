using System.Runtime.InteropServices;
namespace WinSwitch.Power;

// Windows' own power plan API (powrprof.dll), the same calls Windows Settings and powercfg use (SPEC.md › Playing nicely with Windows)
public static class PowerApi
{
    public static readonly Guid NoSubgroup = new("fea3413e-7e05-4911-9a71-700331f1c294");

    public static Guid? ActiveScheme()
    {
        if (PowerGetActiveScheme(IntPtr.Zero, out var schemePointer) != 0) return null;
        try { return Marshal.PtrToStructure<Guid>(schemePointer); } finally { LocalFree(schemePointer); }
    }
    public static bool SetActiveScheme(Guid scheme) => PowerSetActiveScheme(IntPtr.Zero, ref scheme) == 0;

    public static List<Guid> Schemes()
    {
        var schemes = new List<Guid>();
        for (uint index = 0; ; index++)
        {
            var buffer = new byte[16]; var size = (uint)buffer.Length;
            if (PowerEnumerate(IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 16 /* ACCESS_SCHEME */, index, buffer, ref size) != 0) break;
            schemes.Add(new Guid(buffer));
        }
        return schemes;
    }

    public static string SchemeName(Guid scheme)
    {
        uint size = 0;
        var sizeResult = PowerReadFriendlyName(IntPtr.Zero, ref scheme, IntPtr.Zero, IntPtr.Zero, null, ref size);
        if (sizeResult != 0 && sizeResult != 234 /* ERROR_MORE_DATA */ || size == 0) return "";
        var buffer = new byte[size];
        if (PowerReadFriendlyName(IntPtr.Zero, ref scheme, IntPtr.Zero, IntPtr.Zero, buffer, ref size) != 0) return "";
        return System.Text.Encoding.Unicode.GetString(buffer).TrimEnd('\0');
    }

    // A copy of a plan with all its values (hidden ones too), renamed; null when Windows refuses
    public static Guid? Duplicate(Guid source, string name)
    {
        var newPointer = IntPtr.Zero;
        if (PowerDuplicateScheme(IntPtr.Zero, ref source, ref newPointer) != 0) return null;
        Guid copy;
        try { copy = Marshal.PtrToStructure<Guid>(newPointer); } finally { LocalFree(newPointer); }
        var nameBytes = System.Text.Encoding.Unicode.GetBytes(name + "\0");
        PowerWriteFriendlyName(IntPtr.Zero, ref copy, IntPtr.Zero, IntPtr.Zero, nameBytes, (uint)nameBytes.Length);
        return copy;
    }
    public static bool Delete(Guid scheme) => PowerDeleteScheme(IntPtr.Zero, ref scheme) == 0;

    // One value of one plan; null when this PC doesn't have the setting
    public static uint? Read(Guid scheme, Guid subgroup, Guid setting, bool pluggedIn)
    {
        var result = pluggedIn ? PowerReadACValueIndex(IntPtr.Zero, ref scheme, ref subgroup, ref setting, out var acValue) : PowerReadDCValueIndex(IntPtr.Zero, ref scheme, ref subgroup, ref setting, out acValue);
        return result == 0 ? acValue : null;
    }
    public static bool Write(Guid scheme, Guid subgroup, Guid setting, bool pluggedIn, uint value) => (pluggedIn ? PowerWriteACValueIndex(IntPtr.Zero, ref scheme, ref subgroup, ref setting, value) : PowerWriteDCValueIndex(IntPtr.Zero, ref scheme, ref subgroup, ref setting, value)) == 0;

    // Windows 11 power mode (Best power efficiency / Balanced / Best performance). Undocumented, so callers read it back and give up if it doesn't stick
    public static readonly Guid ModeEfficiency = new("961cc777-2547-4f9d-8174-7d86181b8a7a"), ModeBalanced = Guid.Empty, ModePerformance = new("ded574b5-45a0-4f42-8737-46345c09c238");
    public static Guid? PowerMode() { try { return PowerGetEffectiveOverlayScheme(out var mode) == 0 ? mode : null; } catch { return null; } }
    public static bool SetPowerMode(Guid mode) { try { return PowerSetActiveOverlayScheme(mode) == 0 && PowerMode() == mode; } catch { return false; } }

    // Plugged in or not, battery % and whether there is a battery at all
    public static (bool pluggedIn, int batteryPercent, bool hasBattery) PowerStatus()
    {
        if (!GetSystemPowerStatus(out var status)) return (true, 100, false);
        var hasBattery = (status.BatteryFlag & 128) == 0 && status.BatteryFlag != 255;
        return (status.ACLineStatus != 0, status.BatteryLifePercent == 255 ? 100 : status.BatteryLifePercent, hasBattery);
    }
    public static bool CanHibernate() { try { return GetPwrCapabilities(out var capabilities) && capabilities.HiberFilePresent != 0; } catch { return false; } }

    public static bool Sleep(bool hibernate) => SetSuspendState(hibernate, false, false);

    [StructLayout(LayoutKind.Sequential)] public struct SystemPowerStatus { public byte ACLineStatus; public byte BatteryFlag; public byte BatteryLifePercent; public byte SystemStatusFlag; public int BatteryLifeTime; public int BatteryFullLifeTime; }
    [StructLayout(LayoutKind.Sequential)] struct PowerCapabilities
    {
        public byte PowerButtonPresent, SleepButtonPresent, LidPresent, SystemS1, SystemS2, SystemS3, SystemS4, SystemS5, HiberFilePresent, FullWake, VideoDimPresent, ApmPresent, UpsPresent, ThermalControl, ProcessorThrottle, ProcessorMinThrottle, ProcessorMaxThrottle, FastSystemS4;
        public byte Hiberboot, WakeAlarmPresent, AoAc, DiskSpinDown, HiberFileType, AoAcConnectivitySupported, Spare3a, Spare3b, Spare3c, Spare3d, Spare3e, Spare3f;
        public byte SystemBatteriesPresent, BatteriesAreShortTerm;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 9)] public uint[] BatteryScale;
        public int AcOnLineWake, SoftLidWake, RtcWake, MinDeviceWakeState, DefaultLowLatencyWake;
    }
    [DllImport("powrprof.dll")] static extern uint PowerGetActiveScheme(IntPtr rootKey, out IntPtr activeScheme);
    [DllImport("powrprof.dll")] static extern uint PowerSetActiveScheme(IntPtr rootKey, ref Guid scheme);
    [DllImport("powrprof.dll")] static extern uint PowerEnumerate(IntPtr rootKey, IntPtr scheme, IntPtr subgroup, uint accessFlags, uint index, byte[] buffer, ref uint bufferSize);
    [DllImport("powrprof.dll")] static extern uint PowerReadFriendlyName(IntPtr rootKey, ref Guid scheme, IntPtr subgroup, IntPtr setting, byte[]? buffer, ref uint bufferSize);
    [DllImport("powrprof.dll")] static extern uint PowerWriteFriendlyName(IntPtr rootKey, ref Guid scheme, IntPtr subgroup, IntPtr setting, byte[] buffer, uint bufferSize);
    [DllImport("powrprof.dll")] static extern uint PowerDuplicateScheme(IntPtr rootKey, ref Guid source, ref IntPtr destination);
    [DllImport("powrprof.dll")] static extern uint PowerDeleteScheme(IntPtr rootKey, ref Guid scheme);
    [DllImport("powrprof.dll")] static extern uint PowerReadACValueIndex(IntPtr rootKey, ref Guid scheme, ref Guid subgroup, ref Guid setting, out uint value);
    [DllImport("powrprof.dll")] static extern uint PowerReadDCValueIndex(IntPtr rootKey, ref Guid scheme, ref Guid subgroup, ref Guid setting, out uint value);
    [DllImport("powrprof.dll")] static extern uint PowerWriteACValueIndex(IntPtr rootKey, ref Guid scheme, ref Guid subgroup, ref Guid setting, uint value);
    [DllImport("powrprof.dll")] static extern uint PowerWriteDCValueIndex(IntPtr rootKey, ref Guid scheme, ref Guid subgroup, ref Guid setting, uint value);
    [DllImport("powrprof.dll")] static extern uint PowerGetEffectiveOverlayScheme(out Guid mode);
    [DllImport("powrprof.dll")] static extern uint PowerSetActiveOverlayScheme(Guid mode);
    [DllImport("powrprof.dll")] static extern bool SetSuspendState(bool hibernate, bool forceCritical, bool disableWakeEvent);
    [DllImport("powrprof.dll")] static extern bool GetPwrCapabilities(out PowerCapabilities capabilities);
    [DllImport("kernel32.dll")] static extern bool GetSystemPowerStatus(out SystemPowerStatus status);
    [DllImport("kernel32.dll")] static extern IntPtr LocalFree(IntPtr memory);
}
