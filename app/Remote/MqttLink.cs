using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Protocol;
using WinSwitch.Power;
namespace WinSwitch.Remote;

// MQTT for Home Assistant and Node-RED: state on <topic>/<pc>/state, commands on .../<thing>/set, Home Assistant discovery so the PC appears as a device (SPEC.md › MQTT)
public sealed class MqttLink
{
    static App App => App.Instance;
    IMqttClient? client;
    CancellationTokenSource? stopping;
    string lastState = "", lastDiscovery = "";
    readonly HashSet<string> discoveryTopics = new();
    readonly System.Windows.Threading.DispatcherTimer stateTimer = new() { Interval = TimeSpan.FromSeconds(5) };
    public string Status { get; private set; } = "Off";
    public event Action? StatusChanged;
    static string Node => Regex.Replace(Environment.MachineName.ToLowerInvariant(), "[^a-z0-9_-]", "_");
    static string BaseTopic => $"{(App.Settings.Remote.MqttTopic.Trim('/') == "" ? "winswitch" : App.Settings.Remote.MqttTopic.Trim('/'))}/{Node}";

    public MqttLink()
    {
        stateTimer.Tick += async (sender, args) => await PublishStateAsync();
        App.Engine.Changed += () => App.Dispatcher.BeginInvoke(async () => await PublishStateAsync());
        RemoteControl.Acted += () => App.Dispatcher.BeginInvoke(async () => await PublishStateAsync(force: true));
        App.SettingsChanged += () => App.Dispatcher.BeginInvoke(async () => await PublishDiscoveryAsync());
    }

    // (Re)connect with the current settings, or disconnect when MQTT is off
    public async Task ApplyAsync()
    {
        await StopAsync();
        var remote = App.Settings.Remote;
        if (!remote.MqttOn || remote.MqttHost.Trim() == "") { SetStatus(remote.MqttOn ? "Enter the broker's address" : "Off"); return; }
        stopping = new CancellationTokenSource();
        _ = KeepConnectedAsync(stopping.Token);
    }

    public async Task StopAsync()
    {
        stopping?.Cancel(); stateTimer.Stop();
        if (client is { IsConnected: true })
        {
            try { await client.PublishAsync(Message($"{BaseTopic}/availability", "offline", retain: true)); await client.DisconnectAsync(); } catch { }
        }
        client?.Dispose(); client = null; lastState = ""; lastDiscovery = "";
    }

    // Connects, and reconnects every 15 s after the broker goes away
    async Task KeepConnectedAsync(CancellationToken cancel)
    {
        var remote = App.Settings.Remote;
        while (!cancel.IsCancellationRequested)
        {
            try
            {
                client?.Dispose();
                client = new MqttFactory().CreateMqttClient();
                client.ApplicationMessageReceivedAsync += OnMessageAsync;
                var options = new MqttClientOptionsBuilder().WithTcpServer(remote.MqttHost.Trim(), remote.MqttPort).WithClientId($"winswitch-{Node}").WithCleanSession()
                    .WithKeepAlivePeriod(TimeSpan.FromSeconds(30)).WithWillTopic($"{BaseTopic}/availability").WithWillPayload("offline").WithWillRetain(true).WithWillQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce);
                if (remote.MqttUser != "") options.WithCredentials(remote.MqttUser, RemoteControl.MqttPassword);
                if (remote.MqttTls) options.WithTlsOptions(tls => tls.UseTls());
                SetStatus($"Connecting to {remote.MqttHost}…");
                await client.ConnectAsync(options.Build(), cancel);
                await client.SubscribeAsync(new MqttClientSubscribeOptionsBuilder().WithTopicFilter(filter => filter.WithTopic($"{BaseTopic}/#")).WithTopicFilter(filter => filter.WithTopic("homeassistant/status")).Build(), cancel);
                await client.PublishAsync(Message($"{BaseTopic}/availability", "online", retain: true), cancel);
                lastDiscovery = ""; lastState = "";
                await App.Dispatcher.InvokeAsync(async () => { await PublishDiscoveryAsync(); await PublishStateAsync(force: true); stateTimer.Start(); }).Task.Unwrap();
                SetStatus($"Connected to {remote.MqttHost}" + (remote.HomeAssistantDiscovery ? ", Home Assistant can see this PC" : ""));
                RemoteControl.Log("mqtt", "connected", remote.MqttHost);
                while (client.IsConnected && !cancel.IsCancellationRequested) await Task.Delay(2000, cancel);
                if (!cancel.IsCancellationRequested) SetStatus("Lost the connection; trying again…");
            }
            catch (OperationCanceledException) { return; }
            catch (Exception error) { SetStatus($"Can't connect: {error.Message.TrimEnd('.')}. Trying again every 15 seconds."); }
            try { await Task.Delay(15000, cancel); } catch { return; }
        }
    }

    void SetStatus(string status) { Status = status; App.Dispatcher.BeginInvoke(() => StatusChanged?.Invoke()); }

    // Commands: <base>/profile/set "Desk", <base>/tweak/<id>/set ON, <base>/power/set shutdown, <base>/volume/set 30, mute, brightness, awake
    async Task OnMessageAsync(MqttApplicationMessageReceivedEventArgs received)
    {
        var topic = received.ApplicationMessage.Topic; var payload = received.ApplicationMessage.ConvertPayloadToString()?.Trim() ?? "";
        if (topic == "homeassistant/status") { if (payload == "online") await App.Dispatcher.InvokeAsync(async () => { lastDiscovery = ""; await PublishDiscoveryAsync(); await PublishStateAsync(force: true); }).Task.Unwrap(); return; }
        if (!topic.EndsWith("/set") || !topic.StartsWith(BaseTopic + "/")) return;
        var parts = topic[(BaseTopic.Length + 1)..^4].Split('/');
        var on = payload.ToUpperInvariant() is "ON" or "TRUE" or "1";
        (string action, JsonObject body) command = parts[0] switch
        {
            "profile" => ("profile", new JsonObject { ["name"] = payload }),
            "tweak" when parts.Length > 1 => ("tweak", new JsonObject { ["id"] = parts[1], ["on"] = on }),
            "power" => (payload.ToLowerInvariant().Replace(" ", ""), new JsonObject()),
            "volume" => ("volume", new JsonObject { ["level"] = payload }),
            "brightness" => ("brightness", new JsonObject { ["level"] = payload }),
            "mute" => ("mute", new JsonObject { ["on"] = on }),
            "awake" => ("awake", int.TryParse(payload, out var minutes) ? new JsonObject { ["minutes"] = minutes } : new JsonObject { ["on"] = on }),
            _ => ("", new JsonObject()),
        };
        if (command.action != "") await RemoteControl.DoAsync(command.action, command.body, "mqtt");
    }

    // One retained JSON state message, sent when anything in it changes (checked every 5 s, and straight after an action)
    async Task PublishStateAsync(bool force = false)
    {
        if (client is not { IsConnected: true }) return;
        var state = RemoteControl.State(); state.Remove("idleSeconds");
        var json = state.ToJsonString();
        if (!force && json == lastState) return;
        lastState = json;
        state["idleSeconds"] = RemoteControl.State()["idleSeconds"]?.GetValue<int>();
        try { await client.PublishAsync(Message($"{BaseTopic}/state", state.ToJsonString(), retain: true)); } catch { }
    }

    // Home Assistant discovery: a Profile select, a switch per tweak, power buttons, volume and brightness numbers, mute and keep awake switches, sensors
    async Task PublishDiscoveryAsync()
    {
        if (client is not { IsConnected: true }) return;
        var remote = App.Settings.Remote;
        var configs = new Dictionary<string, JsonObject>();
        if (remote.HomeAssistantDiscovery)
        {
            var device = new JsonObject { ["identifiers"] = new JsonArray($"winswitch_{Node}"), ["name"] = Environment.MachineName, ["manufacturer"] = "WinSwitch", ["model"] = "Windows PC", ["sw_version"] = App.Updater.CurrentVersion };
            JsonObject Entity(string id, string name, string? icon = null)
            {
                var entity = new JsonObject { ["name"] = name, ["unique_id"] = $"winswitch_{Node}_{id}", ["object_id"] = $"{Node}_{id}", ["availability_topic"] = $"{BaseTopic}/availability", ["state_topic"] = $"{BaseTopic}/state", ["device"] = device.DeepClone() };
                if (icon != null) entity["icon"] = icon;
                return entity;
            }
            void Add(string component, string id, JsonObject entity) => configs[$"homeassistant/{component}/{Node}/{id}/config"] = entity;
            var allowed = RemoteControl.Actions.Where(action => RemoteControl.Allowed(action.key)).Select(action => action.key).ToHashSet();
            if (allowed.Contains("profile"))
            {
                var select = Entity("profile", "Profile", "mdi:tune-variant"); select["command_topic"] = $"{BaseTopic}/profile/set"; select["value_template"] = "{{ value_json.profile }}";
                select["options"] = new JsonArray(App.Settings.Profiles.Select(profile => (JsonNode)profile.Name).Append("Automatic").ToArray());
                Add("select", "profile", select);
            }
            if (allowed.Contains("tweak"))
                foreach (var tweak in App.Settings.Tweaks)
                {
                    var toggle = Entity($"tweak_{tweak.Id}", tweak.Name, "mdi:weather-night"); toggle["command_topic"] = $"{BaseTopic}/tweak/{tweak.Id}/set"; toggle["value_template"] = $"{{{{ 'ON' if value_json.tweaks['{tweak.Id}'] else 'OFF' }}}}";
                    Add("switch", $"tweak_{tweak.Id}", toggle);
                }
            foreach (var (action, label, icon) in new[] { ("screenoff", "Screen off", "mdi:monitor-off"), ("lock", "Lock", "mdi:lock"), ("sleep", "Sleep", "mdi:power-sleep"), ("hibernate", "Hibernate", "mdi:snowflake"), ("restart", "Restart", "mdi:restart"), ("shutdown", "Shut down", "mdi:power") })
            {
                if (!allowed.Contains(action) || action == "hibernate" && !PowerApi.CanHibernate()) continue;
                var button = Entity(action, label, icon); button.Remove("state_topic"); button["command_topic"] = $"{BaseTopic}/power/set"; button["payload_press"] = action;
                Add("button", action, button);
            }
            if (allowed.Contains("volume")) { var volume = Entity("volume", "Volume", "mdi:volume-high"); volume["command_topic"] = $"{BaseTopic}/volume/set"; volume["value_template"] = "{{ value_json.volume }}"; volume["min"] = 0; volume["max"] = 100; volume["unit_of_measurement"] = "%"; Add("number", "volume", volume); }
            if (allowed.Contains("brightness") && Direct.Brightness() != null) { var brightness = Entity("brightness", "Screen brightness", "mdi:brightness-6"); brightness["command_topic"] = $"{BaseTopic}/brightness/set"; brightness["value_template"] = "{{ value_json.brightness }}"; brightness["min"] = 0; brightness["max"] = 100; brightness["unit_of_measurement"] = "%"; Add("number", "brightness", brightness); }
            if (allowed.Contains("mute")) { var mute = Entity("mute", "Mute", "mdi:volume-off"); mute["command_topic"] = $"{BaseTopic}/mute/set"; mute["value_template"] = "{{ 'ON' if value_json.muted else 'OFF' }}"; Add("switch", "mute", mute); }
            if (allowed.Contains("awake")) { var awake = Entity("awake", "Keep awake", "mdi:coffee"); awake["command_topic"] = $"{BaseTopic}/awake/set"; awake["value_template"] = "{{ 'ON' if value_json.awake else 'OFF' }}"; Add("switch", "awake", awake); }
            var (_, _, hasBattery) = PowerApi.PowerStatus();
            if (hasBattery)
            {
                var battery = Entity("battery", "Battery"); battery["device_class"] = "battery"; battery["unit_of_measurement"] = "%"; battery["value_template"] = "{{ value_json.battery }}"; Add("sensor", "battery", battery);
                var plugged = Entity("plugged_in", "Plugged in"); plugged["device_class"] = "plug"; plugged["value_template"] = "{{ 'ON' if value_json.pluggedIn else 'OFF' }}"; Add("binary_sensor", "plugged_in", plugged);
            }
            var situation = Entity("situation", "Why this profile", "mdi:information-outline"); situation["value_template"] = "{{ value_json.reason }}"; Add("sensor", "situation", situation);
            var idle = Entity("idle", "Idle time", "mdi:timer-sand"); idle["device_class"] = "duration"; idle["unit_of_measurement"] = "s"; idle["value_template"] = "{{ value_json.idleSeconds }}"; Add("sensor", "idle", idle);
            var mac = Entity("mac", "Wired network MAC (for Wake on LAN)", "mdi:ethernet"); mac["entity_category"] = "diagnostic"; mac["value_template"] = "{{ value_json.mac }}"; Add("sensor", "mac", mac);
        }
        var signature = string.Join("|", configs.Select(config => config.Key + config.Value.ToJsonString()));
        if (signature == lastDiscovery) return;
        lastDiscovery = signature;
        try
        {
            // Entities that went away (a deleted tweak, an action turned off) are removed with an empty retained message
            foreach (var gone in discoveryTopics.Where(topic => !configs.ContainsKey(topic)).ToList()) { await client.PublishAsync(Message(gone, "", retain: true)); discoveryTopics.Remove(gone); }
            foreach (var (topic, config) in configs) { await client.PublishAsync(Message(topic, config.ToJsonString(), retain: true)); discoveryTopics.Add(topic); }
        }
        catch { }
    }

    static MqttApplicationMessage Message(string topic, string payload, bool retain) => new MqttApplicationMessageBuilder().WithTopic(topic).WithPayload(payload).WithRetainFlag(retain).WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce).Build();

    public static string StateTopic => $"{BaseTopic}/state";
    public static string CommandTopic(string thing) => $"{BaseTopic}/{thing}/set";
}
