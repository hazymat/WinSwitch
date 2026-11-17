using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using QRCoder;
using WinSwitch.Remote;
using WinSwitch.Settings;
namespace WinSwitch;

// Settings › Remote control: Phone and Shortcuts, Home Assistant, Allowed actions, Remote log (SPEC.md › Remote control)
public partial class SettingsWindow
{
    RemoteSettings Remote => Settings.Remote;

    void WireRemoteTabs()
    {
        FillPhone(); FillMqtt(); FillAllowed();
        refreshLogButton.Click += (sender, args) => LoadRemoteLog();
        openLogButton.Click += (sender, args) => { if (File.Exists(RemoteControl.LogPath)) Process.Start(new ProcessStartInfo("notepad.exe", $"\"{RemoteControl.LogPath}\"")); };
        settingsTabs.SelectionChanged += (sender, args) => { if (args.Source == settingsTabs && settingsTabs.SelectedItem == remoteLogTab) LoadRemoteLog(); };
        App.Mqtt.StatusChanged += OnMqttStatus;
        Closed += (sender, args) => App.Mqtt.StatusChanged -= OnMqttStatus;
    }

    // ---- Phone and Shortcuts ----
    void FillPhone()
    {
        phonePage.Children.Clear();
        phonePage.Children.Add(Callout("Control this PC from your phone on your home network: switch profile, Night on or off, volume, keep awake, sleep or shut down. No app or account needed. It only answers phones on your home network; from outside, use a VPN such as Tailscale."));
        var onBox = new CheckBox { Content = "Turn on the phone page", IsChecked = Remote.PhoneOn, FontWeight = FontWeights.SemiBold };
        onBox.Click += async (sender, args) =>
        {
            Remote.PhoneOn = onBox.IsChecked == true; Settings.Save(); App.Phone.Apply(); FillPhone();
            if (Remote.PhoneOn && !await Firewall.RuleExistsAsync()) await AllowFirewallAsync();
        };
        var portBox = new TextBox { Text = Remote.PhonePort.ToString(), Width = 70, Margin = new Thickness(8, 0, 0, 0) };
        portBox.LostFocus += (sender, args) => { if (int.TryParse(portBox.Text, out var port) && port is >= 1024 and <= 65535 && port != Remote.PhonePort) { Remote.PhonePort = port; Settings.Save(); App.Phone.Apply(); FillPhone(); } };
        var status = App.Phone.Running ? Text($"On at http://{RemoteControl.LanAddress()}:{App.Phone.Port}", 12.5, false, new SolidColorBrush(Color.FromRgb(0x1D, 0x6B, 0x35)), new Thickness(0, 6, 0, 0)) : Text(App.Phone.Problem != "" ? App.Phone.Problem : "Off", 12.5, false, App.Phone.Problem != "" ? new SolidColorBrush(Color.FromRgb(0xB2, 0x6B, 0x00)) : quietBrush, new Thickness(0, 6, 0, 0));
        phonePage.Children.Add(Card(new StackPanel { Children = { onBox, status, new Expander { Header = "Port", Style = (Style)FindResource("More"), Content = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0), Children = { Text("Listen on port", 13, false, null, new Thickness(0, 3, 0, 0)), portBox } } } } }));
        if (!App.Phone.Running) return;
        var token = RemoteControl.EnsureToken();
        var pageUrl = $"http://{RemoteControl.LanAddress()}:{App.Phone.Port}/p/{token}/";
        // Pair a phone: QR code and three steps
        var steps = new StackPanel { Margin = new Thickness(18, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        steps.Children.Add(Text("Pair your phone", 14.5, true, null, new Thickness(0, 0, 0, 8)));
        foreach (var step in new[] { "1.  Point your iPhone's camera at the code.", "2.  Tap the link: Safari opens this PC's page.", "3.  Share › Add to Home Screen, and it's an app." }) steps.Children.Add(Text(step, 13, false, null, new Thickness(0, 0, 0, 4)));
        steps.Children.Add(new WrapPanel { Margin = new Thickness(0, 10, 0, 0), Children = { Button("Open it on this PC", () => Process.Start(new ProcessStartInfo($"http://127.0.0.1:{App.Phone.Port}/p/{token}/") { UseShellExecute = true })), Button("New code (unpairs every phone)", () => { if (MessageBox.Show(this, "Make a new code? Every phone and shortcut that uses the old one stops working until you pair it again.", "WinSwitch", MessageBoxButton.OKCancel) != MessageBoxResult.OK) return; RemoteControl.NewToken(); FillPhone(); }) } });
        var qr = new Image { Width = 190, Height = 190, Source = QrImage(pageUrl) };
        RenderOptions.SetBitmapScalingMode(qr, BitmapScalingMode.NearestNeighbor);
        var pair = new DockPanel { Children = { qr, steps } }; DockPanel.SetDock(qr, Dock.Left);
        phonePage.Children.Add(Card(pair));
        // Windows Firewall
        var firewallText = Text("Checking Windows Firewall…", 13, false, quietBrush);
        var firewallButtons = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        phonePage.Children.Add(Card(new StackPanel { Children = { Text("Windows Firewall", 14.5, true, null, new Thickness(0, 0, 0, 6)), firewallText, firewallButtons } }));
        _ = ShowFirewallAsync(firewallText, firewallButtons);
        // Siri and Shortcuts
        phonePage.Children.Add(Card(ShortcutsHelp(token)));
    }

    async Task ShowFirewallAsync(TextBlock text, WrapPanel buttons)
    {
        var allowed = await Firewall.RuleExistsAsync();
        text.Text = allowed ? "Phones on your home network are allowed in (private networks only)." : "Windows Firewall may block phones. Allow WinSwitch on home (private) networks; Windows asks for permission once.";
        buttons.Children.Clear();
        if (!allowed) buttons.Children.Add(Button("Allow on home networks…", async () => { await AllowFirewallAsync(); await ShowFirewallAsync(text, buttons); }, true));
    }
    async Task AllowFirewallAsync() { if (!await Firewall.AllowAsync(Remote.PhonePort)) MessageBox.Show(this, "The firewall rule wasn't added. If phones can't connect, press Allow on home networks again.", "WinSwitch"); }

    static BitmapImage QrImage(string text)
    {
        using var generator = new QRCodeGenerator();
        var png = new PngByteQRCode(generator.CreateQrCode(text, QRCodeGenerator.ECCLevel.M)).GetGraphic(10);
        var image = new BitmapImage(); image.BeginInit(); image.StreamSource = new MemoryStream(png); image.CacheOption = BitmapCacheOption.OnLoad; image.EndInit(); image.Freeze();
        return image;
    }

    // Copyable recipes for Shortcuts' "Get contents of URL" (and curl, Stream Deck, anything that can POST)
    StackPanel ShortcutsHelp(string token)
    {
        var help = new StackPanel();
        help.Children.Add(Text("Siri and Shortcuts", 14.5, true, null, new Thickness(0, 0, 0, 6)));
        help.Children.Add(Text("In the Shortcuts app, add Get contents of URL, set Method to POST, add the header and the JSON body below. Then \"Hey Siri, laptop to Desk\" works. Copy gives you all of it.", 12.5, false, quietBrush, new Thickness(0, 0, 0, 10)));
        var address = $"http://{RemoteControl.LanAddress()}:{App.Phone.Port}/api/";
        var firstProfile = Settings.Profiles.FirstOrDefault(profile => !profile.IsStarting)?.Name ?? "Desk";
        var firstTweak = Settings.Tweaks.FirstOrDefault()?.Name ?? "Night";
        var examples = new (string label, string action, string body)[] { ($"Switch to {firstProfile}", "profile", $"{{\"name\": \"{firstProfile}\"}}"), ("Back to automatic", "profile", "{\"name\": \"Automatic\"}"), ($"{firstTweak} on", "tweak", $"{{\"name\": \"{firstTweak}\", \"on\": true}}"), ("Volume to 30%", "volume", "{\"level\": 30}"), ("Volume down 10%", "volume", "{\"change\": -10}"), ("Keep awake for an hour", "awake", "{\"minutes\": 60}"), ("Sleep", "sleep", ""), ("Shut down", "shutdown", ""), ("What's it doing? (GET, no body)", "state", "") };
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 8) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(200) }); grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        foreach (var (label, action, body) in examples)
        {
            grid.RowDefinitions.Add(new RowDefinition());
            var row = grid.RowDefinitions.Count - 1;
            var recipe = $"URL: {address}{action}\nMethod: {(action == "state" ? "GET" : "POST")}\nHeader: Authorization = Bearer {token}" + (body == "" ? "" : $"\nRequest body (JSON): {body}");
            var labelText = Text(label, 13, false, null, new Thickness(0, 5, 8, 5)); Grid.SetRow(labelText, row); grid.Children.Add(labelText);
            var shown = new TextBlock { Text = $"{address}{action}" + (body == "" ? "" : "   " + body), FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 11.5, Foreground = quietBrush, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
            Grid.SetRow(shown, row); Grid.SetColumn(shown, 1); grid.Children.Add(shown);
            var copy = Button("Copy", () => Clipboard.SetText(recipe)); copy.Padding = new Thickness(10, 2, 10, 2); copy.Margin = new Thickness(8, 2, 0, 2);
            Grid.SetRow(copy, row); Grid.SetColumn(copy, 2); grid.Children.Add(copy);
        }
        help.Children.Add(grid);
        help.Children.Add(new WrapPanel { Children = { Button("Copy the key (token)", () => Clipboard.SetText(token)), Text("Keep it private: anyone on your network with it can control this PC.", 12, false, quietBrush, new Thickness(4, 5, 0, 0)) } });
        return help;
    }

    // ---- Home Assistant (MQTT) ----
    // Automation: the MQTT broker connection, then Home Assistant (on or off) and the raw topics for Node-RED and other tools (Mat 17 Nov)
    void FillMqtt()
    {
        mqttPage.Children.Clear();
        // MQTT broker
        var onBox = new CheckBox { Content = "Connect to an MQTT broker", IsChecked = Remote.MqttOn, FontWeight = FontWeights.SemiBold };
        var hostBox = new TextBox { Text = Remote.MqttHost, Width = 240 }; var portBox = new TextBox { Text = Remote.MqttPort.ToString(), Width = 70 };
        var userBox = new TextBox { Text = Remote.MqttUser, Width = 240 }; var passwordBox = new PasswordBox { Password = RemoteControl.MqttPassword, Width = 240 };
        var tlsBox = new CheckBox { Content = "Use TLS (encrypted)", IsChecked = Remote.MqttTls };
        var topicBox = new TextBox { Text = Remote.MqttTopic, Width = 240 };
        var fields = new Grid { Margin = new Thickness(0, 10, 0, 0) };
        fields.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) }); fields.ColumnDefinitions.Add(new ColumnDefinition());
        void Field(string label, UIElement input) { fields.RowDefinitions.Add(new RowDefinition()); var row = fields.RowDefinitions.Count - 1; var caption = Text(label, 13, false, null, new Thickness(0, 6, 0, 6)); Grid.SetRow(caption, row); fields.Children.Add(caption); ((FrameworkElement)input).HorizontalAlignment = HorizontalAlignment.Left; ((FrameworkElement)input).Margin = new Thickness(0, 4, 0, 4); Grid.SetRow(input, row); Grid.SetColumn(input, 1); fields.Children.Add(input); }
        Field("Broker address", hostBox); Field("Port", portBox); Field("User name", userBox); Field("Password", passwordBox);
        var more = new Expander { Header = "More", Style = (Style)FindResource("More"), Content = new StackPanel { Children = { tlsBox, new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0), Children = { Text("Topic starts with", 13, false, null, new Thickness(0, 3, 10, 0)), topicBox } } } } };
        mqttStatusText = Text(App.Mqtt.Status, 12.5, false, quietBrush, new Thickness(0, 10, 0, 0));
        var connect = Button(Remote.MqttOn ? "Save and reconnect" : "Save", async () =>
        {
            Remote.MqttOn = onBox.IsChecked == true; Remote.MqttHost = hostBox.Text.Trim(); Remote.MqttPort = int.TryParse(portBox.Text, out var port) ? port : 1883; Remote.MqttUser = userBox.Text.Trim(); RemoteControl.MqttPassword = passwordBox.Password;
            Remote.MqttTls = tlsBox.IsChecked == true; Remote.MqttTopic = topicBox.Text.Trim() == "" ? "winswitch" : topicBox.Text.Trim();
            Settings.Save(); await App.Mqtt.ApplyAsync(); FillMqtt();
        }, true);
        connect.HorizontalAlignment = HorizontalAlignment.Left; connect.Margin = new Thickness(0, 12, 0, 0);
        mqttPage.Children.Add(Card(new StackPanel { Children = { Text("MQTT broker", 14.5, true, null, new Thickness(0, 0, 0, 8)), onBox, fields, more, connect, mqttStatusText } }));
        // Home Assistant
        var haBox = new CheckBox { Content = "Show this PC in Home Assistant", IsChecked = Remote.HomeAssistantDiscovery, FontWeight = FontWeights.SemiBold, IsEnabled = Remote.MqttOn };
        haBox.Click += async (sender, args) => { Remote.HomeAssistantDiscovery = haBox.IsChecked == true; Settings.Save(); await App.Mqtt.ApplyAsync(); };
        var ha = new StackPanel { Children = { Text("Home Assistant", 14.5, true, null, new Thickness(0, 0, 0, 8)), haBox, Text(Remote.MqttOn ? "Profile picker, a switch per tweak, power buttons, volume, battery." : "Connect to your MQTT broker first (for example Home Assistant's Mosquitto add-on).", 12.5, false, quietBrush, new Thickness(20, 2, 0, 0)) } };
        var mac = RemoteControl.WiredMac();
        ha.Children.Add(new Expander { Header = "Turning the PC on", Style = (Style)FindResource("More"), Margin = new Thickness(0, 10, 0, 0), Content = Text(mac == "" ? "Only possible over a network cable (Wake on LAN). This PC has no wired network adapter now." : $"Use Home Assistant's Wake on LAN integration with {mac}. Wake on LAN must be on in the network adapter (the helper can turn it on).", 12.5, false, quietBrush, new Thickness(0, 6, 0, 0)) });
        mqttPage.Children.Add(Card(ha));
        // Topics, for Node-RED and anything else
        var topics = new StackPanel();
        foreach (var (what, topic, payload) in new[] { ("State (JSON, kept)", MqttLink.StateTopic, ""), ("Switch profile", MqttLink.CommandTopic("profile"), "Desk, or Automatic"), ("Tweak on or off", MqttLink.CommandTopic("tweak/<id>"), "ON or OFF"), ("Power", MqttLink.CommandTopic("power"), "screenoff, lock, sleep, hibernate, restart, shutdown"), ("Volume", MqttLink.CommandTopic("volume"), "0 to 100"), ("Mute", MqttLink.CommandTopic("mute"), "ON or OFF"), ("Keep awake", MqttLink.CommandTopic("awake"), "ON, OFF, or minutes") })
            topics.Children.Add(new WrapPanel { Margin = new Thickness(0, 2, 0, 2), Children = { Text(what, 12.5, false, null, new Thickness(0, 0, 10, 0)), new TextBlock { Text = topic, FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 11.5, Margin = new Thickness(0, 1, 10, 0) }, Text(payload, 12, false, quietBrush) } });
        mqttPage.Children.Add(Card(new Expander { Header = "Topics for Node-RED and other tools", Style = (Style)FindResource("More"), Margin = new Thickness(0), Content = topics }));
    }
    TextBlock? mqttStatusText;
    void OnMqttStatus() { if (mqttStatusText != null) mqttStatusText.Text = App.Mqtt.Status; }

    // ---- Allowed actions ----
    void FillAllowed()
    {
        allowedPage.Children.Clear();
        allowedPage.Children.Add(Callout("What the phone page, Shortcuts and Home Assistant may do. Every action is logged under Remote log."));
        var list = new StackPanel();
        foreach (var (key, label) in RemoteControl.Actions)
        {
            var box = new CheckBox { Content = label, IsChecked = RemoteControl.Allowed(key) };
            box.Click += (sender, args) => { Remote.Blocked.Remove(key); if (box.IsChecked != true) Remote.Blocked.Add(key); Settings.Save(); App.NotifySettingsChanged(); };
            list.Children.Add(box);
        }
        allowedPage.Children.Add(Card(list));
    }

    void LoadRemoteLog()
    {
        try { var lines = File.Exists(RemoteControl.LogPath) ? File.ReadAllLines(RemoteControl.LogPath) : Array.Empty<string>(); remoteLogText.Text = lines.Length == 0 ? "Nothing yet. Every remote command is listed here: when, from where, what, and whether it worked." : string.Join(Environment.NewLine, lines.TakeLast(500).Reverse()); }
        catch (Exception error) { remoteLogText.Text = error.Message; }
    }
}
