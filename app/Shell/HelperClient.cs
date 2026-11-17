using System.IO;
using System.IO.Pipes;
using System.Text.Json;
using System.Text.Json.Nodes;
namespace WinSwitch.Shell;

// Talks to the optional helper service over its local pipe; every call fails quietly when the helper isn't installed (SPEC.md › Architecture)
public static class HelperClient
{
    public const string PipeName = "WinSwitchHelper";

    public static async Task<JsonNode?> SendAsync(string command, object? arguments = null, int timeoutMilliseconds = 15000)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await pipe.ConnectAsync(400);
            var request = arguments == null ? new JsonObject() : JsonSerializer.SerializeToNode(arguments)!.AsObject();
            request["command"] = command;
            using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
            using var reader = new StreamReader(pipe, leaveOpen: true);
            await writer.WriteLineAsync(request.ToJsonString());
            var replyTask = reader.ReadLineAsync();
            if (await Task.WhenAny(replyTask, Task.Delay(timeoutMilliseconds)) != replyTask) return null;
            var reply = JsonNode.Parse(await replyTask ?? "{}");
            if (reply?["ok"]?.GetValue<bool>() != true) { CrashReporter.Write($"Helper refused {command}: {reply?["error"]}", null, false); return null; }
            return reply;
        }
        catch { return null; }
    }

    public static async Task<bool> DoAsync(string command, object? arguments = null) => await SendAsync(command, arguments) != null;
    public static async Task<string> VersionAsync() => (await SendAsync("version", null, 1500))?["value"]?.GetValue<string>() ?? "";

    // ---- Installed or not (the service is registered), and the version bundled with this build
    public static bool Installed { get { try { using var service = new System.ServiceProcess.ServiceController(ServiceName); _ = service.Status; return true; } catch { return false; } } }
    public static bool Running { get { try { using var service = new System.ServiceProcess.ServiceController(ServiceName); return service.Status == System.ServiceProcess.ServiceControllerStatus.Running; } catch { return false; } } }
    const string ServiceName = "WinSwitchHelper";
    public static string BundledVersion => System.Diagnostics.FileVersionInfo.GetVersionInfo(ExtractBundled()).FileVersion is { } version && Version.TryParse(version, out var parsed) ? parsed.ToString(3) : "";
    public static bool NeedsUpdate(string installedVersion) => Version.TryParse(installedVersion, out var installed) && Version.TryParse(BundledVersion, out var bundled) && bundled > installed;

    static string ExtractBundled()
    {
        var folder = Path.Combine(Path.GetTempPath(), "WinSwitch-helper"); Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "WinSwitchHelper.exe");
        using var resource = typeof(HelperClient).Assembly.GetManifestResourceStream("WinSwitch.Helper.exe")!;
        try { using var file = File.Create(path); resource.CopyTo(file); } catch (IOException) { } // already there and in use
        return path;
    }

    // One admin prompt: the helper copies itself into Program Files and registers the service (also how it updates)
    public static async Task<bool> InstallAsync()
    {
        try
        {
            using var elevated = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(ExtractBundled(), "--install") { UseShellExecute = true, Verb = "runas", WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden })!;
            await elevated.WaitForExitAsync();
            if (elevated.ExitCode != 0) return false;
        }
        catch { return false; } // admin prompt declined
        for (var attempt = 0; attempt < 20 && await VersionAsync() == ""; attempt++) await Task.Delay(250);
        await FillInSettingsAsync();
        return true;
    }

    // The helper puts back what it changed (device power, Wake on LAN, hibernate, fast startup), then removes itself
    public static async Task<bool> RemoveAsync()
    {
        if (await DoAsync("uninstall")) { for (var attempt = 0; attempt < 40 && Installed; attempt++) await Task.Delay(250); return !Installed; }
        try { using var elevated = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WinSwitch Helper", "WinSwitchHelper.exe"), "--remove") { UseShellExecute = true, Verb = "runas" })!; await elevated.WaitForExitAsync(); }
        catch { return false; }
        for (var attempt = 0; attempt < 40 && Installed; attempt++) await Task.Delay(250);
        return !Installed;
    }

    // ---- Profile settings that go through the helper
    public static async Task<JsonNode?> StateAsync() => (await SendAsync("state", null, 20000))?["value"];

    public static async Task<int?> ReadSettingAsync(string key)
    {
        if (!Installed || await StateAsync() is not { } state) return null;
        if (key == "wakeInput") { var flags = state["devices"]!.AsArray().Where(device => device?["kind"]?.GetValue<string>() == "input" && device?["allowWake"] != null).Select(device => device!["allowWake"]!.GetValue<bool>()).ToList(); return flags.Count == 0 ? null : flags.Any(flag => flag) ? 1 : 0; }
        if (key == "wakeOnLan") { var adapters = state["wakeOnLan"]!.AsArray(); return adapters.Count == 0 ? null : adapters.Any(adapter => adapter?["on"]?.GetValue<bool>() == true) ? 1 : 0; }
        return null;
    }

    public static async Task<bool> WriteSettingAsync(string key, int value) => key switch
    {
        "wakeInput" => await DoAsync("wake-devices", new { kind = "input", on = value == 1 }), // devices that refuse are skipped (built-in keyboards often do)
        "wakeOnLan" => await DoAsync("wol", new { on = value == 1 }) && await ReadSettingAsync(key) == value,
        _ => false,
    };

    // After installing, the device rows Windows wouldn't show before get their current values in Starting profile and Original settings (SPEC.md › Starting profile)
    public static async Task FillInSettingsAsync()
    {
        var snapshot = App.Instance.Snapshot; var starting = App.Instance.Settings.Profiles.FirstOrDefault(profile => profile.IsStarting);
        foreach (var setting in Power.Catalog.All.Where(setting => setting.Kind == Power.SettingKind.Helper))
        {
            App.Instance.Engine.NotSupported.Remove(setting.Key);
            if (await ReadSettingAsync(setting.Key) is not { } value) continue;
            if (snapshot != null && !snapshot.Values.ContainsKey(setting.Key)) snapshot.Values[setting.Key] = new Settings.SidePair { Ac = value };
            if (starting != null && !starting.Values.ContainsKey(setting.Key)) starting.Values[setting.Key] = new Settings.SidePair { Ac = value };
        }
        snapshot?.Save(); App.Instance.Settings.Save();
    }
}
