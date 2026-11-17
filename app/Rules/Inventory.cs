using System.Management;
using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using WinSwitch.Shell;
namespace WinSwitch.Rules;

// What is connected right now, as "things" a situation can match: monitors by their EDID id, network adapters by MAC, USB/Bluetooth/HID devices by
// instance id, and the network by its router's hardware address (SPEC.md › Situations). Docks have no identity of their own, so a dock is the set of things it brings
public class Inventory
{
    public Dictionary<string, string> Things { get; } = new(StringComparer.OrdinalIgnoreCase); // thing -> readable name
    public string NetworkThing { get; private set; } = "";
    public string NetworkName { get; private set; } = "";

    public static Inventory Take()
    {
        var inventory = new Inventory();
        foreach (var monitor in MonitorSetup.Current().Where(monitor => !monitor.Internal)) inventory.Things["monitor:" + monitor.Id] = monitor.Name;
        try
        {
            foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces().Where(adapter => adapter.NetworkInterfaceType is NetworkInterfaceType.Ethernet or NetworkInterfaceType.Wireless80211 or NetworkInterfaceType.GigabitEthernet))
            {
                var mac = adapter.GetPhysicalAddress().ToString();
                if (mac.Length == 12 && mac != "000000000000") inventory.Things["net:" + mac] = adapter.Description;
            }
        }
        catch { }
        foreach (var (instance, name) in DeviceNames(externalOnly: true)) inventory.Things["device:" + instance] = name;
        (inventory.NetworkThing, inventory.NetworkName) = CurrentNetwork();
        return inventory;
    }

    // Present plug-and-play devices by instance id. externalOnly keeps the kinds a dock or a plugged-in device brings (USB, Bluetooth, HID, sound, cameras)
    public static Dictionary<string, string> DeviceNames(bool externalOnly = false)
    {
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT PNPDeviceID, Name, PNPClass, Present FROM Win32_PnPEntity");
            foreach (ManagementObject device in searcher.Get()) using (device)
            {
                if (device["PNPDeviceID"] is not string instance || device["Present"] is false) continue;
                var deviceClass = device["PNPClass"] as string ?? "";
                if (externalOnly && !(instance.StartsWith(@"USB\", StringComparison.OrdinalIgnoreCase) || instance.StartsWith(@"BTHENUM\", StringComparison.OrdinalIgnoreCase) || deviceClass is "Bluetooth" or "Mouse" or "Keyboard")) continue;
                if (externalOnly && instance.Contains("ROOT_HUB", StringComparison.OrdinalIgnoreCase)) continue;
                names[instance] = device["Name"] as string ?? instance;
            }
        }
        catch { }
        return names;
    }

    // The network we're on, recognised by its router's hardware address: Windows only shows Wi-Fi names to apps with Location on (SPEC.md › Situations)
    static (string thing, string name) CurrentNetwork()
    {
        try
        {
            foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces().Where(adapter => adapter.OperationalStatus == OperationalStatus.Up && adapter.NetworkInterfaceType != NetworkInterfaceType.Loopback))
            {
                var gateway = adapter.GetIPProperties().GatewayAddresses.Select(address => address.Address).FirstOrDefault(address => address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && !address.Equals(IPAddress.Any));
                if (gateway == null) continue;
                var macBytes = new byte[6]; var length = macBytes.Length;
                if (SendARP(BitConverter.ToInt32(gateway.GetAddressBytes(), 0), 0, macBytes, ref length) != 0) continue;
                var connection = adapter.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 ? "Wi-Fi" : "Wired";
                return ("network:" + Convert.ToHexString(macBytes, 0, length), $"{connection} network (router {gateway})");
            }
        }
        catch { }
        return ("", "");
    }
    [DllImport("iphlpapi.dll")] static extern int SendARP(int destination, int source, byte[] macAddress, ref int length);
}
