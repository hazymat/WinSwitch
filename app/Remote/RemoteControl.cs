using System.IO;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using WinSwitch.Power;
using WinSwitch.Settings;
using WinSwitch.Shell;
namespace WinSwitch.Remote;

// What the phone page, Shortcuts and MQTT can do and see; one place so all three behave the same (SPEC.md › Remote control)
public static class RemoteControl
{
    static App App => App.Instance;
    static AppSettings Settings => App.Settings;

    // Every action, with the words Settings › Allowed actions shows
    public static readonly (string key, string label)[] Actions =
    {
        ("profile", "Switch profile (or back to Automatic)"), ("tweak", "Turn tweaks on and off (e.g. Night)"), ("volume", "Volume"), ("mute", "Mute"), ("brightness", "Laptop screen brightness"), ("awake", "Keep awake"),
        ("screenoff", "Screen off"), ("lock", "Lock"), ("sleep", "Sleep"), ("hibernate", "Hibernate"), ("restart", "Restart"), ("shutdown", "Shut down"),
    };
    public static readonly string[] PowerActions = { "screenoff", "lock", "sleep", "hibernate", "restart", "shutdown" };
    public static bool Allowed(string action) => !Settings.Remote.Blocked.Contains(action);
    public static event Action? Acted; // so MQTT can publish the new state straight away

    // ---- State: one JSON object (MQTT state topic, the phone page, Shortcuts) ----
    public static JsonObject State()
    {
        var engine = App.Engine; var (pluggedIn, percent, hasBattery) = PowerApi.PowerStatus();
        var tweaks = new JsonObject(); foreach (var tweak in Settings.Tweaks) tweaks[tweak.Id] = engine.TweakIsOn(tweak);
        var state = new JsonObject
        {
            ["pc"] = Environment.MachineName, ["version"] = App.Updater.CurrentVersion,
            ["profile"] = Settings.Power.Paused ? "Paused" : engine.ActiveProfile?.Name ?? "", ["automatic"] = engine.Automatic && !Settings.Power.Paused, ["reason"] = engine.Reason,
            ["profiles"] = new JsonArray(Settings.Profiles.Select(profile => (JsonNode)new JsonObject { ["name"] = profile.Name, ["colour"] = profile.Colour }).ToArray()),
            ["tweaks"] = tweaks, ["tweakList"] = new JsonArray(Settings.Tweaks.Select(tweak => (JsonNode)new JsonObject { ["id"] = tweak.Id, ["name"] = tweak.Name, ["on"] = engine.TweakIsOn(tweak) }).ToArray()),
            ["pluggedIn"] = pluggedIn, ["hasBattery"] = hasBattery, ["battery"] = hasBattery ? percent : null,
            ["volume"] = Direct.Volume(), ["muted"] = Direct.Muted(), ["brightness"] = Direct.Brightness(),
            ["awake"] = Shell.PowerActions.Awake, ["awakeUntil"] = Shell.PowerActions.AwakeUntil is { } until && until != DateTime.MaxValue ? until.ToString("HH:mm") : null,
            ["idleSeconds"] = IdleSeconds(), ["signedIn"] = true, ["mac"] = WiredMac(), ["canHibernate"] = PowerApi.CanHibernate(),
            ["allowed"] = new JsonArray(Actions.Where(action => Allowed(action.key)).Select(action => (JsonNode)action.key).ToArray()),
        };
        return state;
    }

    // ---- Doing: always on the UI thread, checked against Allowed actions, logged ----
    public static async Task<(bool ok, string message)> DoAsync(string action, JsonObject? body, string who)
    {
        if (!Actions.Any(known => known.key == action)) return (false, "Unknown action");
        if (!Allowed(action)) { Log(who, action, "refused (turned off in Allowed actions)"); return (false, "That action is turned off in WinSwitch › Settings › Allowed actions"); }
        var (ok, message) = await App.Dispatcher.InvokeAsync(() => Do(action, body)).Task;
        Log(who, action + (body is { Count: > 0 } ? " " + body.ToJsonString() : ""), ok ? "done" : "failed: " + message);
        if (ok) Acted?.Invoke();
        return (ok, message);
    }

    static (bool, string) Do(string action, JsonObject? body)
    {
        string Text(string key) => body?[key]?.ToString() ?? "";
        int? Number(string key) => body?[key] is { } node && int.TryParse(node.ToString(), out var number) ? number : null;
        bool? Flag(string key) => body?[key]?.ToString().ToLowerInvariant() switch { "true" or "on" or "1" => true, "false" or "off" or "0" => false, _ => null };
        switch (action)
        {
            case "profile":
                var name = Text("name");
                if (name.Equals("automatic", StringComparison.OrdinalIgnoreCase)) { App.Engine.GoAutomatic(); return (true, "Automatic"); }
                var profile = Settings.Profiles.FirstOrDefault(candidate => candidate.Name.Equals(name, StringComparison.OrdinalIgnoreCase) || candidate.Id == name);
                if (profile == null) return (false, $"No profile called {name}");
                App.Engine.HoldProfile(profile.Id); return (true, profile.Name);
            case "tweak":
                var tweak = Settings.Tweaks.FirstOrDefault(candidate => candidate.Name.Equals(Text("name"), StringComparison.OrdinalIgnoreCase) || candidate.Id == Text("name") || candidate.Id == Text("id"));
                if (tweak == null) return (false, "No such tweak");
                App.Engine.SetTweak(tweak, Flag("on") ?? !App.Engine.TweakIsOn(tweak)); return (true, tweak.Name);
            case "volume":
                var level = Number("level") ?? (Number("change") is { } change ? (Direct.Volume() ?? 0) + change : null);
                return level == null ? (false, "Give level (0 to 100) or change (e.g. -10)") : (Shell.Osd.SetVolume(Math.Clamp(level.Value, 0, 100)), $"{Math.Clamp(level.Value, 0, 100)}%");
            case "mute": return (Shell.Osd.SetMuted(Flag("on") ?? !(Direct.Muted() ?? false)), "");
            case "brightness": return Number("level") is { } brightness ? (Shell.Osd.SetBrightness(Math.Clamp(brightness, 0, 100)), $"{Math.Clamp(brightness, 0, 100)}%") : (false, "Give level (0 to 100)");
            case "awake":
                var minutes = Number("minutes");
                if (Flag("on") == false || minutes == 0 && body?["minutes"] != null) Shell.PowerActions.StopKeepingAwake();
                else Shell.PowerActions.KeepAwake(minutes is > 0 ? TimeSpan.FromMinutes(minutes.Value) : null);
                return (true, "");
            // Power actions run a moment later, so the reply gets back to the phone first
            case "screenoff": Later(Shell.PowerActions.ScreenOff); return (true, "");
            case "lock": Later(Shell.PowerActions.Lock); return (true, "");
            case "sleep": Later(Shell.PowerActions.Sleep); return (true, "");
            case "hibernate": if (!PowerApi.CanHibernate()) return (false, "Hibernate isn't available on this PC"); Later(Shell.PowerActions.Hibernate); return (true, "");
            case "restart": Later(Shell.PowerActions.Restart); return (true, "");
            case "shutdown": Later(Shell.PowerActions.ShutDown); return (true, "");
        }
        return (false, "Unknown action");
    }
    static void Later(Action action) => _ = Task.Delay(1500).ContinueWith(task => App.Dispatcher.Invoke(action));

    // ---- Token and addresses ----
    public static string EnsureToken() { if (Settings.Remote.Token == "") NewToken(); return Settings.Remote.Token; }
    public static void NewToken() { Settings.Remote.Token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(24)).Replace('+', '-').Replace('/', '_').TrimEnd('='); Settings.Save(); }
    public static bool TokenMatches(string given) { var token = Settings.Remote.Token; return token != "" && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(given), Encoding.UTF8.GetBytes(token)); }

    // The PC's address on the home network (the adapter with a router), for the QR code
    public static string LanAddress()
    {
        try
        {
            foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces().Where(adapter => adapter.OperationalStatus == OperationalStatus.Up && adapter.NetworkInterfaceType != NetworkInterfaceType.Loopback).OrderBy(adapter => adapter.NetworkInterfaceType == NetworkInterfaceType.Ethernet ? 0 : 1))
            {
                var properties = adapter.GetIPProperties();
                if (!properties.GatewayAddresses.Any(gateway => gateway.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && !gateway.Address.Equals(System.Net.IPAddress.Any))) continue;
                var address = properties.UnicastAddresses.FirstOrDefault(unicast => unicast.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork);
                if (address != null) return address.Address.ToString();
            }
        }
        catch { }
        return "127.0.0.1";
    }

    // Home network, link-local, and Tailscale-style (100.64/10) addresses only; anything else is refused even if a router forwards the port
    public static bool IsLocal(System.Net.IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (System.Net.IPAddress.IsLoopback(address)) return true;
        if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6) return address.IsIPv6LinkLocal || address.IsIPv6UniqueLocal || address.IsIPv6SiteLocal;
        var bytes = address.GetAddressBytes();
        return bytes[0] == 10 || bytes[0] == 172 && bytes[1] is >= 16 and <= 31 || bytes[0] == 192 && bytes[1] == 168 || bytes[0] == 169 && bytes[1] == 254 || bytes[0] == 100 && bytes[1] is >= 64 and <= 127;
    }

    // Published so Home Assistant's Wake on LAN can turn the PC on
    public static string WiredMac()
    {
        try
        {
            var adapter = NetworkInterface.GetAllNetworkInterfaces().Where(adapter => adapter.NetworkInterfaceType is NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet && adapter.GetPhysicalAddress().GetAddressBytes().Length == 6).OrderBy(adapter => adapter.OperationalStatus == OperationalStatus.Up ? 0 : 1).FirstOrDefault();
            return adapter == null ? "" : string.Join(":", adapter.GetPhysicalAddress().GetAddressBytes().Select(part => part.ToString("X2")));
        }
        catch { return ""; }
    }

    public static string MqttPassword
    {
        get { try { return Settings.Remote.MqttPasswordProtected == "" ? "" : Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(Settings.Remote.MqttPasswordProtected), null, DataProtectionScope.CurrentUser)); } catch { return ""; } }
        set => Settings.Remote.MqttPasswordProtected = value == "" ? "" : Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(value), null, DataProtectionScope.CurrentUser));
    }

    static int IdleSeconds() { var info = new LastInputInfo { Size = (uint)Marshal.SizeOf<LastInputInfo>() }; return GetLastInputInfo(ref info) ? (int)((uint)Environment.TickCount - info.Time) / 1000 : 0; }

    public static string LogPath => Path.Combine(AppPaths.LogsFolder, "remote.log");
    static readonly object logLock = new();
    public static void Log(string who, string what, string result) { try { lock (logLock) File.AppendAllText(LogPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {who,-22} {what}  →  {result}{Environment.NewLine}"); } catch { } }

    [StructLayout(LayoutKind.Sequential)] struct LastInputInfo { public uint Size; public uint Time; }
    [DllImport("user32.dll")] static extern bool GetLastInputInfo(ref LastInputInfo info);
}
