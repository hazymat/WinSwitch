using System.Diagnostics;
using System.IO;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using WinSwitch.Shell;
namespace WinSwitch;

// Settings › App › Helper: install, update or remove the optional service, and the PC-wide things only it can change (SPEC.md › Architecture)
public partial class SettingsWindow
{
    static readonly string helperLog = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "WinSwitch", "helper.log");
    static readonly Brush goodBrush = new SolidColorBrush(Color.FromRgb(0x1D, 0x6B, 0x35)), amberBrush = new SolidColorBrush(Color.FromRgb(0xB2, 0x6B, 0x00));

    void WireHelperTab() => settingsTabs.SelectionChanged += (sender, args) => { if (args.Source == settingsTabs && settingsTabs.SelectedItem == helperTab) _ = FillHelperAsync(); };

    async Task FillHelperAsync()
    {
        helperPage.Children.Clear();
        helperPage.Children.Add(Callout("The helper is a small Windows service for the few things Windows only lets an administrator change. It's optional: WinSwitch does everything else without it. Removing it puts back everything it changed."));
        var installed = HelperClient.Installed;
        var version = installed ? await HelperClient.VersionAsync() : "";
        var bundled = HelperClient.BundledVersion;
        // Status and the one button that fits
        var status = new StackPanel();
        status.Children.Add(Text("Helper", 14.5, true, null, new Thickness(0, 0, 0, 6)));
        status.Children.Add(!installed ? Text("Not installed", 13, false, quietBrush) : version == "" ? Text("Installed, but not running. Try installing it again.", 13, false, amberBrush) : Text($"Installed and running (version {version})", 13, false, goodBrush));
        var buttons = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
        if (!installed || version == "") buttons.Children.Add(Button(installed ? "Install again" : "Install the helper", () => _ = InstallHelperAsync(), true));
        else if (HelperClient.NeedsUpdate(version)) buttons.Children.Add(Button($"Update the helper to {bundled}", () => _ = InstallHelperAsync(), true));
        if (installed) buttons.Children.Add(Button("Remove the helper", () => _ = RemoveHelperAsync()));
        if (File.Exists(helperLog)) buttons.Children.Add(Button("Open its log", () => Process.Start(new ProcessStartInfo("notepad.exe", $"\"{helperLog}\""))));
        status.Children.Add(buttons);
        if (!installed) status.Children.Add(Text("Windows asks for admin permission once. It installs to C:\\Program Files\\WinSwitch Helper.", 12, false, quietBrush, new Thickness(0, 8, 0, 0)));
        helperPage.Children.Add(Card(status));
        // What it adds
        var adds = new StackPanel();
        adds.Children.Add(Text("What it adds", 14.5, true, null, new Thickness(0, 0, 0, 8)));
        foreach (var line in new[] { "Stop the mouse or keyboard waking the laptop in your bag (per profile, under Wireless and devices)", "Wake on LAN, so Home Assistant can turn this PC on", "Stop services and apps that run as administrator (Apps to stop)", "Hibernate and fast startup, and which devices can wake this PC (below)" })
            adds.Children.Add(new DockPanel { Margin = new Thickness(0, 0, 0, 5), Children = { new TextBlock { Text = "\uE73E", FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 12, Foreground = installed ? goodBrush : quietBrush, Margin = new Thickness(0, 2, 8, 0) }, Text(line, 13) } });
        helperPage.Children.Add(Card(adds));
        if (!installed || version == "" || await HelperClient.StateAsync() is not { } state) return;
        // This PC: hibernate, fast startup, devices allowed to wake it
        var thisPc = new StackPanel();
        thisPc.Children.Add(Text("This PC", 14.5, true, null, new Thickness(0, 0, 0, 8)));
        thisPc.Children.Add(HelperBox("Hibernate available", "Lets Hibernate appear in the Power menu and be used when the lid closes or the battery runs low.", state["hibernate"]?.GetValue<bool>() == true, on => HelperClient.DoAsync("hibernate", new { on })));
        thisPc.Children.Add(HelperBox("Fast startup", "Starts Windows faster, but Shut down is then a kind of hibernate. Turn it off if devices or updates misbehave after starting.", state["fastStartup"]?.GetValue<bool>() == true, on => HelperClient.DoAsync("fast-startup", new { on })));
        var wakers = state["devices"]!.AsArray().Where(device => device?["allowWake"] != null).ToList();
        if (wakers.Count > 0)
        {
            var list = new StackPanel { Margin = new Thickness(0, 6, 0, 0) };
            foreach (var device in wakers)
            {
                var id = device!["id"]!.GetValue<string>();
                var box = new CheckBox { Content = $"{device["name"]!.GetValue<string>()}  ({KindWord(device["kind"]!.GetValue<string>())})", IsChecked = device["allowWake"]!.GetValue<bool>(), Margin = new Thickness(0, 0, 0, 5) };
                box.Click += async (sender, args) => { if (!await HelperClient.DoAsync("device", new { id, allowWake = box.IsChecked == true })) box.IsChecked = !box.IsChecked; };
                list.Children.Add(box);
            }
            thisPc.Children.Add(new Expander { Header = $"Devices that can wake this PC ({wakers.Count(device => device!["allowWake"]!.GetValue<bool>())} of {wakers.Count})", Style = (Style)FindResource("More"), Margin = new Thickness(0, 8, 0, 0), Content = list });
        }
        helperPage.Children.Add(Card(thisPc));
    }

    static string KindWord(string kind) => kind switch { "input" => "mouse or keyboard", "ethernet" => "wired network", "wifi" => "Wi-Fi", "bluetooth" => "Bluetooth", "usbhub" => "USB hub", _ => "other" };

    UIElement HelperBox(string title, string note, bool on, Func<bool, Task<bool>> change)
    {
        var box = new CheckBox { Content = title, IsChecked = on, FontWeight = FontWeights.SemiBold };
        box.Click += async (sender, args) => { if (!await change(box.IsChecked == true)) { box.IsChecked = !box.IsChecked; MessageBox.Show(this, $"The helper couldn't change {title.ToLowerInvariant()}. Its log says why.", "WinSwitch"); } };
        return new StackPanel { Margin = new Thickness(0, 0, 0, 10), Children = { box, Text(note, 12, false, quietBrush, new Thickness(20, 2, 0, 0)) } };
    }

    async Task InstallHelperAsync()
    {
        helperPage.Children.Insert(1, Text("Installing… (approve the Windows prompt)", 13, false, quietBrush, new Thickness(0, 0, 0, 8)));
        if (!await HelperClient.InstallAsync()) MessageBox.Show(this, "The helper wasn't installed. Windows needs admin permission for it; nothing was changed.", "WinSwitch");
        await FillHelperAsync();
    }

    async Task RemoveHelperAsync()
    {
        if (MessageBox.Show(this, "Remove the helper? It puts back the device, Wake on LAN, hibernate and fast startup settings it found when it was installed, then removes itself.", "WinSwitch", MessageBoxButton.OKCancel) != MessageBoxResult.OK) return;
        if (!await HelperClient.RemoveAsync()) MessageBox.Show(this, "The helper couldn't remove itself. Try again, and approve the Windows prompt if one appears.", "WinSwitch");
        await FillHelperAsync();
    }
}
