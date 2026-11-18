using System.Runtime.InteropServices;
namespace WinSwitch.Remote;

// Windows' saved Wi-Fi networks and the one connected now, by profile name (normally the network's name, its SSID), through the documented WLAN API
public static class WifiNetworks
{
    public static List<string> Saved()
    {
        var names = new List<string>();
        Each((client, guid) =>
        {
            if (WlanGetProfileList(client, ref guid, IntPtr.Zero, out var list) != 0) return;
            try { var count = Marshal.ReadInt32(list); for (var index = 0; index < count; index++) names.Add(Marshal.PtrToStringUni(list + 8 + index * 516) ?? ""); }
            finally { WlanFreeMemory(list); }
        });
        return names.Where(name => name != "").Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    // "" when not on Wi-Fi
    public static string Connected()
    {
        var connected = "";
        Each((client, guid) =>
        {
            if (connected != "" || WlanQueryInterface(client, ref guid, 7 /* current_connection */, IntPtr.Zero, out _, out var data, IntPtr.Zero) != 0) return;
            try { if (Marshal.ReadInt32(data) == 1 /* connected */) connected = Marshal.PtrToStringUni(data + 8) ?? ""; }
            finally { WlanFreeMemory(data); }
        });
        return connected;
    }

    static void Each(Action<IntPtr, Guid> action)
    {
        try
        {
            if (WlanOpenHandle(2, IntPtr.Zero, out _, out var client) != 0) return;
            try
            {
                if (WlanEnumInterfaces(client, IntPtr.Zero, out var interfaces) != 0) return;
                try { var count = Marshal.ReadInt32(interfaces); for (var index = 0; index < count; index++) action(client, Marshal.PtrToStructure<Guid>(interfaces + 8 + index * 532)); }
                finally { WlanFreeMemory(interfaces); }
            }
            finally { WlanCloseHandle(client, IntPtr.Zero); }
        }
        catch { } // no Wi-Fi service (desktops, servers)
    }

    [DllImport("wlanapi.dll")] static extern int WlanOpenHandle(int clientVersion, IntPtr reserved, out int negotiatedVersion, out IntPtr client);
    [DllImport("wlanapi.dll")] static extern int WlanCloseHandle(IntPtr client, IntPtr reserved);
    [DllImport("wlanapi.dll")] static extern int WlanEnumInterfaces(IntPtr client, IntPtr reserved, out IntPtr interfaceList);
    [DllImport("wlanapi.dll")] static extern int WlanGetProfileList(IntPtr client, ref Guid interfaceGuid, IntPtr reserved, out IntPtr profileList);
    [DllImport("wlanapi.dll")] static extern int WlanQueryInterface(IntPtr client, ref Guid interfaceGuid, int opCode, IntPtr reserved, out int dataSize, out IntPtr data, IntPtr opcodeValueType);
    [DllImport("wlanapi.dll")] static extern void WlanFreeMemory(IntPtr memory);
}
