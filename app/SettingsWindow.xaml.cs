using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using WinSwitch.Power;
using WinSwitch.Rules;
using WinSwitch.Settings;
using WinSwitch.Shell;
namespace WinSwitch;

// Changes apply immediately (no OK/Apply), as the sibling apps. Pages are built from the catalog (SPEC.md › Settings window, Simple and powerful)
public partial class SettingsWindow : Window
{
    App App => App.Instance;
    AppSettings Settings => App.Settings;
    string selectedProfileId = "", selectedTweakId = "";
    bool startingUnlocked; // until this window closes
    static readonly Brush quietBrush = new SolidColorBrush(Color.FromRgb(0x66, 0x6B, 0x75)), accentBrush = new SolidColorBrush(Color.FromRgb(0x2F, 0x7F, 0xD8)), lineBrush = new SolidColorBrush(Color.FromRgb(0xE3, 0xE5, 0xE8));

    public SettingsWindow()
    {
        InitializeComponent();
        settingsTabs.SelectedItem = profilesTab;
        WireGeneralTab(); WireAboutTab(); WireRemoteTabs(); WireHelperTab();
        FillProfiles(); FillTweaks(); FillSituations(); _ = FillOriginalAsync();
        KeyDown += (sender, keyArgs) => { if (keyArgs.Key == Key.Escape && !keyArgs.Handled && keyArgs.OriginalSource is not TextBox) { keyArgs.Handled = true; Close(); } };
        App.SettingsChanged += OnOutsideChange; App.Engine.Changed += OnEngineChanged;
        Closed += (sender, args) => { App.SettingsChanged -= OnOutsideChange; App.Engine.Changed -= OnEngineChanged; App.Engine.ThingsAppeared -= OnThingsAppeared; };
        settingsTabs.SelectionChanged += (sender, args) => { if (args.Source == settingsTabs && settingsTabs.SelectedItem == originalTab) _ = FillOriginalAsync(); };
    }
    // Never rebuilt under an open drop-down or a box being typed in: it waits until you've finished
    void OnOutsideChange() => Dispatcher.BeginInvoke(async () =>
    {
        for (var waited = 0; waited < 120 && IsLoaded && (Mouse.Captured != null || Keyboard.FocusedElement is TextBox); waited++) await Task.Delay(500);
        if (!editing && IsLoaded) { FillProfiles(); FillTweaks(); FillSituations(); }
    });
    // Only when what's on changes (the engine also reports every 30 s), so open drop-downs and text boxes aren't rebuilt under you
    string lastEngineState = "";
    void OnEngineChanged() => Dispatcher.BeginInvoke(() =>
    {
        var state = $"{App.Engine.ActiveProfile?.Id}|{App.Engine.SituationId}|{Settings.Power.Paused}|{string.Join(",", App.Engine.ActiveTweaks.Select(tweak => tweak.Id))}|{App.Engine.Current.Things.Count}";
        if (state == lastEngineState) return;
        lastEngineState = state; FillProfileList(); FillTweakList(); if (learnPanel == null) FillSituations();
    });
    bool editing;
    // Our own edits save and re-evaluate without rebuilding the page under the pointer
    void Changed() { editing = true; try { Settings.Save(); App.Engine.EvaluateSoon(); FillProfileList(); FillTweakList(); } finally { editing = false; } }

    public void SelectTab(string tabName, string profileId = "")
    {
        var tabByName = new Dictionary<string, TabItem> { ["Profiles"] = profilesTab, ["Tweaks"] = tweaksTab, ["Situations"] = situationsTab, ["Original"] = originalTab, ["Phone"] = phoneTab, ["HomeAssistant"] = mqttTab, ["Automation"] = mqttTab, ["Allowed"] = allowedTab, ["RemoteLog"] = remoteLogTab, ["General"] = generalTab, ["Helper"] = helperTab, ["About"] = aboutTab };
        if (tabByName.TryGetValue(tabName, out var selectedTab)) settingsTabs.SelectedItem = selectedTab;
        if (profileId != "") { selectedProfileId = profileId; FillProfiles(); }
    }

    // ---- Small builders ----
    static TextBlock Text(string text, double size = 13, bool bold = false, Brush? colour = null, Thickness? margin = null) => new() { Text = text, FontSize = size, FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal, Foreground = colour ?? Brushes.Black, TextWrapping = TextWrapping.Wrap, Margin = margin ?? new Thickness(0) };
    Border Card(UIElement content) => new() { Style = (Style)FindResource("Card"), Child = content };
    Border Callout(string text, bool warning = false) => new() { Margin = new Thickness(0, 10, 0, 12), Style = (Style)FindResource(warning ? "Warning" : "Callout"), Child = new DockPanel { Children = { new TextBlock { Style = (Style)FindResource(warning ? "WarningIcon" : "CalloutIcon") }, new TextBlock { Style = (Style)FindResource("CalloutText"), Text = text } } } };
    static Button Button(string label, Action action, bool primary = false) { var button = new Button { Content = label }; if (primary) button.Style = (Style)Application.Current.FindResource("Primary"); button.Click += (sender, args) => action(); return button; }
    static TextBlock Link(string label, Action action) { var link = new TextBlock { Text = label, Foreground = accentBrush, Cursor = Cursors.Hand, Margin = new Thickness(0, 6, 0, 0) }; link.MouseLeftButtonUp += (sender, args) => action(); return link; }
    static Brush BrushOf(string colour) { try { return (Brush)new BrushConverter().ConvertFromString(colour)!; } catch { return Brushes.SteelBlue; } }
    static Border Dot(string colour, double size = 10) => new() { Width = size, Height = size, CornerRadius = new CornerRadius(size / 2), Background = BrushOf(colour), Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };

    // ---- Profiles (SPEC.md › Profiles, Simple and powerful) ----
    void FillProfiles() { FillProfileList(); FillProfileEditor(); }

    void FillProfileList()
    {
        profileList.Children.Clear();
        if (Settings.ProfileById(selectedProfileId) == null) selectedProfileId = App.Engine.ActiveProfile?.Id ?? Settings.Profiles.FirstOrDefault()?.Id ?? "";
        foreach (var profile in Settings.Profiles)
        {
            var isOn = !Settings.Power.Paused && App.Engine.ActiveProfile?.Id == profile.Id;
            var header = new DockPanel();
            // Right side: ON NOW, or a green play button that switches to it now
            if (isOn) { var badge = new Border { Background = new SolidColorBrush(Color.FromRgb(0xE3, 0xF4, 0xE8)), CornerRadius = new CornerRadius(9), Padding = new Thickness(7, 1, 7, 2), VerticalAlignment = VerticalAlignment.Center, Child = Text("ON NOW", 10, true, new SolidColorBrush(Color.FromRgb(0x1D, 0x6B, 0x35))) }; DockPanel.SetDock(badge, Dock.Right); header.Children.Add(badge); }
            else { var play = PlayButton(() => App.Engine.HoldProfile(profile.Id)); DockPanel.SetDock(play, Dock.Right); header.Children.Add(play); }
            header.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Children = { Dot(profile.Colour), Text(profile.Name, 13.5, true) } });
            var content = new StackPanel { Children = { header, Text(Describe(profile), 12, false, quietBrush, new Thickness(0, 4, 0, 0)), TurnsOnLine(profile) } };
            // Duplicate and delete sit on the card, shown on the chosen one
            var tools = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 6, -4, -4), Visibility = profile.Id == selectedProfileId ? Visibility.Visible : Visibility.Collapsed };
            tools.Children.Add(IconTool("\uE8C8", "Make a copy", true, () => DuplicateProfile(profile)));
            if (!profile.IsStarting && Settings.Profiles.Count > 1) tools.Children.Add(IconTool("\uE74D", "Delete this profile", true, () => DeleteProfile(profile)));
            content.Children.Add(tools);
            var card = new Border { Style = (Style)FindResource("Card"), Margin = new Thickness(0, 0, 0, 8), Padding = new Thickness(12, 10, 12, 10), Cursor = Cursors.Hand, Child = content };
            if (profile.Id == selectedProfileId) { card.BorderBrush = accentBrush; card.BorderThickness = new Thickness(2); }
            card.MouseLeftButtonUp += (sender, args) => { if (args.Handled) return; selectedProfileId = profile.Id; FillProfiles(); };
            profileList.Children.Add(card);
        }
        profileList.Children.Add(Link("＋ New profile", () => { var source = Settings.ProfileById(selectedProfileId); var profile = new Profile { Name = "New profile", Colour = Templates.Colours[Settings.Profiles.Count % Templates.Colours.Length], Values = source?.IsStarting == false ? source.Values.ToDictionary(entry => entry.Key, entry => entry.Value.Clone()) : new(), SpeedAc = source?.IsStarting == false ? source.SpeedAc : null, SpeedDc = source?.IsStarting == false ? source.SpeedDc : null }; Settings.Profiles.Add(profile); selectedProfileId = profile.Id; Settings.Save(); FillProfiles(); }));
        profileList.Children.Add(Link("✨ Set up with the wizard", App.OpenWizard));
    }

    // What turns this profile on, gathered from Automatic switching: "⚡ Home dock connected · otherwise"; rules that can never win are left out
    UIElement TurnsOnLine(Profile profile)
    {
        var reasons = Settings.Situations.Where(situation => situation.ProfileId == profile.Id && Engine.CoveredBy(Settings.Situations, situation) == null).Select(Engine.Words).ToList();
        if (Settings.DefaultProfileId == profile.Id) reasons.Add(Settings.Situations.Count == 0 ? "always" : "otherwise");
        var line = new TextBlock { FontSize = 11.5, Margin = new Thickness(0, 6, 0, 0), TextWrapping = TextWrapping.Wrap, Foreground = reasons.Count == 0 ? quietBrush : new SolidColorBrush(Color.FromRgb(0x2F, 0x6F, 0xB8)) };
        line.Inlines.Add(new System.Windows.Documents.Run("\uE945 ") { FontFamily = new FontFamily("Segoe MDL2 Assets") });
        line.Inlines.Add(reasons.Count == 0 ? "Only when picked by hand" : "Turns on: " + string.Join(" · ", reasons));
        return line;
    }

    // A round green play button: "use this profile now"
    Border PlayButton(Action action)
    {
        var green = new SolidColorBrush(Color.FromRgb(0x2E, 0xA0, 0x5A));
        var play = new Border { Width = 30, Height = 30, CornerRadius = new CornerRadius(15), Background = green, Cursor = Cursors.Hand, ToolTip = "Use this profile now", VerticalAlignment = VerticalAlignment.Center, Child = new TextBlock { Text = "\uE768", FontFamily = new FontFamily("Segoe MDL2 Assets"), Foreground = Brushes.White, FontSize = 12, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 0, 0, 0) } };
        play.MouseEnter += (sender, args) => play.Background = new SolidColorBrush(Color.FromRgb(0x25, 0x8A, 0x4C)); play.MouseLeave += (sender, args) => play.Background = green;
        play.MouseLeftButtonUp += (sender, args) => { args.Handled = true; action(); };
        return play;
    }

    void DuplicateProfile(Profile source)
    {
        var copy = new Profile { Name = source.IsStarting ? "My profile" : source.Name + " copy", Colour = Templates.Colours[Settings.Profiles.Count % Templates.Colours.Length], Values = source.Values.ToDictionary(entry => entry.Key, entry => entry.Value.Clone()), SpeedAc = source.SpeedAc, SpeedDc = source.SpeedDc, AppsToStop = source.AppsToStop.Select(app => new AppToStop { Path = app.Path, Name = app.Name, OnBatteryOnly = app.OnBatteryOnly }).ToList(), SoundOutput = source.SoundOutput, SoundOutputName = source.SoundOutputName };
        Settings.Profiles.Insert(Settings.Profiles.IndexOf(source) + 1, copy); selectedProfileId = copy.Id; Changed(); FillProfiles();
    }

    void DeleteProfile(Profile profile)
    {
        if (MessageBox.Show(this, $"Delete the {profile.Name} profile?", "WinSwitch", MessageBoxButton.OKCancel) != MessageBoxResult.OK) return;
        Settings.Profiles.Remove(profile);
        foreach (var situation in Settings.Situations.Where(situation => situation.ProfileId == profile.Id)) situation.ProfileId = Settings.DefaultProfileId;
        if (Settings.DefaultProfileId == profile.Id) Settings.DefaultProfileId = Settings.Profiles[0].Id;
        selectedProfileId = ""; Changed(); FillProfiles(); FillSituations();
    }

    // One readable line per profile: "Screen off after 30 minutes, never sleeps, fastest, Wi-Fi off"
    string Describe(Profile profile)
    {
        var parts = new List<string>();
        string Sides(string key, Func<int, string> words) { var pair = profile.Get(key); if (pair == null) return ""; var ac = pair.Ac is { } acValue ? words(acValue) : ""; var dc = pair.Dc is { } dcValue ? words(dcValue) : ""; return ac == dc || dc == "" ? ac : ac == "" ? dc + " on battery" : $"{ac} ({dc} on battery)"; }
        var screen = Sides("screenOff", seconds => seconds == 0 ? "screen stays on" : "screen off after " + Catalog.TimeLabel(seconds).Replace(" minutes", " min").Replace(" minute", " min")); if (screen != "") parts.Add(screen);
        var sleep = Sides("sleepAfter", seconds => seconds == 0 ? "never sleeps" : "sleeps after " + Catalog.TimeLabel(seconds).Replace(" minutes", " min").Replace(" minute", " min")); if (sleep != "") parts.Add(sleep);
        if (profile.SpeedAc is { } speedAc) parts.Add(Catalog.SpeedLabels[speedAc].ToLowerInvariant() + (profile.SpeedDc is { } speedDc && speedDc != speedAc ? $" ({Catalog.SpeedLabels[speedDc].ToLowerInvariant()} on battery)" : ""));
        else if (profile.SpeedDc is { } onlyDc) parts.Add(Catalog.SpeedLabels[onlyDc].ToLowerInvariant() + " on battery");
        if (profile.Get("wifi")?.Ac == 0) parts.Add("Wi-Fi off");
        if (profile.Get("bluetooth")?.Ac == 0) parts.Add("Bluetooth off");
        if (profile.Get("volume")?.Ac is { } volume) parts.Add($"volume {volume}%");
        if (profile is Tweak { VolumeChange: { } change and not 0 }) parts.Add($"volume {(change > 0 ? "up" : "down")} {Math.Abs(change)}%");
        if (profile.IsStarting) return "How this PC was before WinSwitch.";
        if (parts.Count == 0) return "Changes nothing yet.";
        var sentence = string.Join(", ", parts) + ".";
        return char.ToUpperInvariant(sentence[0]) + sentence[1..];
    }

    void FillProfileEditor()
    {
        profileEditor.Children.Clear();
        var profile = Settings.ProfileById(selectedProfileId); if (profile == null) return;
        // A suggestion waiting (SPEC.md › Suggestions): Accept or No thanks, never asked twice
        if (Settings.Suggestions.Waiting is { } suggestion)
        {
            var suggestionText = new TextBlock { Style = (Style)FindResource("CalloutText"), Text = suggestion.Text };
            var answers = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0), Children = { Button("Accept", () => { App.Suggestions.Answer(true); FillProfiles(); }, true), Button("No thanks", () => { App.Suggestions.Answer(false); FillProfileEditor(); }) } };
            var body = new StackPanel { Children = { suggestionText, answers } };
            profileEditor.Children.Add(new Border { Margin = new Thickness(0, 0, 0, 12), Style = (Style)FindResource("Callout"), Child = new DockPanel { Children = { new TextBlock { Style = (Style)FindResource("CalloutIcon") }, body } } });
        }
        // Name, colour, Anything else, delete
        var nameBox = new TextBox { Text = profile.Name, FontSize = 17, FontWeight = FontWeights.SemiBold, MinWidth = 240, Padding = new Thickness(4, 2, 4, 2), IsReadOnly = profile.IsStarting };
        nameBox.TextChanged += (sender, args) => { if (nameBox.Text.Trim() != "") { profile.Name = nameBox.Text.Trim(); Changed(); } };
        var swatches = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        foreach (var colour in Templates.Colours) { var swatch = Dot(colour, 18); swatch.Cursor = Cursors.Hand; swatch.Margin = new Thickness(0, 0, 6, 0); if (colour == profile.Colour) { swatch.BorderBrush = Brushes.Black; swatch.BorderThickness = new Thickness(2); } swatch.MouseLeftButtonUp += (sender, args) => { profile.Colour = colour; Changed(); FillProfileEditor(); }; swatches.Children.Add(swatch); }
        var headRow = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
        headRow.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Children = { nameBox, swatches } });
        profileEditor.Children.Add(headRow);
        // Starting profile is locked (greyed) until Edit and a warning, so it can be trusted to mean "how it was" (Mat 17 Nov)
        var locked = profile.IsStarting && !startingUnlocked;
        if (locked)
        {
            var editButton = Button("Edit…", () =>
            {
                if (MessageBox.Show(this, "Starting profile is how your PC was set up when WinSwitch was installed. If you change it, switching to it won't put things back exactly as they were.\n\nUninstalling WinSwitch still puts back the original settings. WinSwitch keeps its own copy of those, under Original settings.\n\nEdit Starting profile?", "Edit Starting profile", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;
                startingUnlocked = true; FillProfileEditor();
            });
            profileEditor.Children.Add(new Border { Style = (Style)FindResource("Callout"), Margin = new Thickness(0, 0, 0, 12), Child = new DockPanel { Children = { new TextBlock { Style = (Style)FindResource("CalloutIcon"), Text = "\uE72E" }, editButton, new TextBlock { Style = (Style)FindResource("CalloutText"), Text = "This is how your PC was set up when WinSwitch was installed. Switching to it puts those settings back." } } } });
            DockPanel.SetDock(editButton, Dock.Right); editButton.Margin = new Thickness(10, 0, 0, 0); editButton.VerticalAlignment = VerticalAlignment.Center;
        }
        else if (profile.IsStarting) profileEditor.Children.Add(Callout("Change it if you like. Even if you do, WinSwitch keeps its own copy of the settings it captured when it was installed, and puts those back if you uninstall it. You can see them under Original settings.", warning: true));
        AddQuestionsCallout(profileEditor);
        var dialsPanel = new StackPanel { IsEnabled = !locked, Opacity = locked ? 0.55 : 1 };
        profileEditor.Children.Add(dialsPanel);
        AddVisualDials(dialsPanel, profile);
        if (!profile.IsStarting) AddAppsToStop(profileEditor, profile);
    }

    // Apps to stop: each with Always / On battery, and Add apps… opening the combined chooser (SPEC.md › Apps to stop)
    void AddAppsToStop(Panel page, Profile profile)
    {
        var body = new StackPanel();
        var title = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
        title.Children.Add(new Border { Width = 26, Height = 26, CornerRadius = new CornerRadius(6), Background = new SolidColorBrush(Color.FromRgb(0xEE, 0xF4, 0xFC)), Margin = new Thickness(0, 0, 9, 0), Child = new TextBlock { Text = "\uE71A", FontFamily = new FontFamily("Segoe MDL2 Assets"), Foreground = accentBrush, FontSize = 12, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } });
        title.Children.Add(Text("Apps to stop", 14.5, true, null, new Thickness(0, 3, 0, 0)));
        body.Children.Add(title);
        // Set them all at once
        if (profile.AppsToStop.Count > 1)
        {
            var allRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
            allRow.Children.Add(Text("Set all to", 12.5, false, quietBrush, new Thickness(0, 0, 8, 0)));
            foreach (var (label, onBattery) in new[] { ("Always", false), ("On battery", true) })
            {
                var link = new TextBlock { Text = label, Foreground = accentBrush, Cursor = Cursors.Hand, FontSize = 12.5, Margin = new Thickness(0, 0, 12, 0) };
                link.MouseLeftButtonUp += (sender, args) => { foreach (var app in profile.AppsToStop) app.OnBatteryOnly = onBattery; Changed(); FillProfileEditor(); };
                allRow.Children.Add(link);
            }
            body.Children.Add(allRow);
        }
        if (profile.AppsToStop.Count == 0) body.Children.Add(Text("Background apps that drain the battery, such as sync tools, updaters or RGB lighting. They start again when you switch to a profile that doesn't stop them.", 12.5, false, quietBrush, new Thickness(0, 0, 0, 8)));
        foreach (var app in profile.AppsToStop.ToList())
        {
            var when = new ComboBox { Width = 130, Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Items = { new ComboBoxItem { Content = "Always", Tag = false }, new ComboBoxItem { Content = "On battery", Tag = true } } };
            when.SelectedIndex = app.OnBatteryOnly ? 1 : 0;
            when.SelectionChanged += (sender, args) => { app.OnBatteryOnly = when.SelectedIndex == 1; Changed(); };
            var remove = new TextBlock { Text = "✕", Foreground = quietBrush, FontSize = 13, Cursor = Cursors.Hand, Margin = new Thickness(12, 0, 2, 0), VerticalAlignment = VerticalAlignment.Center, ToolTip = "Don't stop this app" };
            remove.MouseLeftButtonUp += (sender, args) => { profile.AppsToStop.Remove(app); Changed(); FillProfileEditor(); };
            var icon = new Image { Width = 22, Height = 22, Source = Apps.AppIcon.For(app.Path), Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
            var row = new DockPanel { Margin = new Thickness(0, 4, 0, 4) };
            DockPanel.SetDock(icon, Dock.Left); DockPanel.SetDock(remove, Dock.Right); DockPanel.SetDock(when, Dock.Right);
            row.Children.Add(icon); row.Children.Add(remove); row.Children.Add(when);
            var stoppedNow = Settings.Power.Stopped.Any(stopped => stopped.Path.Equals(app.Path, StringComparison.OrdinalIgnoreCase));
            row.Children.Add(new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children = { Text(app.Name + (stoppedNow ? "  ·  stopped now" : ""), 13, false), new TextBlock { Text = app.Path, FontSize = 11.5, Foreground = quietBrush, TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = app.Path } } });
            body.Children.Add(row);
        }
        var addButton = Button("Add apps…", () =>
        {
            var picker = new Apps.AppPicker(profile.AppsToStop.Select(app => app.Path)) { Owner = this };
            if (picker.ShowDialog() != true) return;
            profile.AppsToStop.AddRange(picker.Chosen);
            Changed(); FillProfileEditor();
        });
        addButton.Margin = new Thickness(0, 8, 0, 0); addButton.HorizontalAlignment = HorizontalAlignment.Left;
        body.Children.Add(addButton);
        page.Children.Add(Card(body));
    }

    // Changes made in Windows that are still waiting for an answer (SPEC.md › Playing nicely with Windows)
    void AddQuestionsCallout(Panel page)
    {
        var waiting = App.Notices.Unanswered.Where(question => question.Key.StartsWith("changed:")).ToList();
        foreach (var question in waiting)
        {
            var row = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
            foreach (var (label, action) in question.Buttons) row.Children.Add(Button(label, () => { action(); App.Notices.Unanswered.Remove(question); FillProfiles(); }, row.Children.Count == 0));
            page.Children.Add(new Border { Style = (Style)FindResource("Callout"), Margin = new Thickness(0, 0, 0, 12), Child = new StackPanel { Children = { Text("Changed in Windows: " + question.Title.Replace("You changed ", "") + " " + question.Text, 13, false, new SolidColorBrush(Color.FromRgb(0x2B, 0x3A, 0x4E))), row } } });
        }
    }

    // The five dials: a few plain choices on each, every real setting one click under it (SPEC.md › Simple and powerful)
    void AddDials(Panel page, Profile profile)
    {
        var notSupported = App.Engine.NotSupported;
        bool Available(SettingDef setting) => !notSupported.Contains(setting.Key) && (App.Snapshot?.Values.ContainsKey(setting.Key) ?? true);
        var glyphs = new Dictionary<string, string> { [Catalog.Screen] = "", [Catalog.Sleep] = "", [Catalog.Speed] = "", [Catalog.Wireless] = "", [Catalog.Sound] = "" };
        foreach (var dial in Catalog.Dials)
        {
            var settings = Catalog.All.Where(setting => setting.Dial == dial && Available(setting)).ToList();
            if (settings.Count == 0) continue;
            var body = new StackPanel();
            var title = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
            title.Children.Add(new Border { Width = 26, Height = 26, CornerRadius = new CornerRadius(6), Background = new SolidColorBrush(Color.FromRgb(0xEE, 0xF4, 0xFC)), Margin = new Thickness(0, 0, 9, 0), Child = new TextBlock { Text = glyphs[dial], FontFamily = new FontFamily("Segoe MDL2 Assets"), Foreground = accentBrush, FontSize = 13, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } });
            title.Children.Add(Text(dial, 14.5, true, null, new Thickness(0, 3, 0, 0)));
            body.Children.Add(title);
            var face = dial == Catalog.Speed ? new List<SettingDef>() : settings.Where(setting => setting.OnDialFace).ToList();
            var under = settings.Except(face).ToList();
            if (dial == Catalog.Speed) AddSpeedDial(body, title, profile, under);
            if (face.Count > 0) body.Children.Add(RowsGrid(profile, face, true));
            if (under.Count > 0)
            {
                var expander = new Expander { Header = $"What this sets ({under.Count})", Style = (Style)FindResource("More"), Content = RowsGrid(profile, under, false) };
                body.Children.Add(expander);
            }
            page.Children.Add(Card(body));
        }
    }

    // Battery or speed: one slider per side sets eight or so rows together; a row changed by hand turns the dial Custom
    void AddSpeedDial(StackPanel body, StackPanel title, Profile profile, List<SettingDef> rows)
    {
        var customPill = new Border { Background = new SolidColorBrush(Color.FromRgb(0xFF, 0xF3, 0xDD)), CornerRadius = new CornerRadius(9), Padding = new Thickness(8, 1, 8, 1), Margin = new Thickness(10, 3, 0, 0), VerticalAlignment = VerticalAlignment.Top, Child = Text("● Custom", 10.5, true, new SolidColorBrush(Color.FromRgb(0x9A, 0x5A, 0x00))) };
        var undoLink = Link("Undo custom", () => { if (profile.SpeedAc != null) Templates.ApplySpeed(profile, true, profile.SpeedAc); if (profile.SpeedDc != null) Templates.ApplySpeed(profile, false, profile.SpeedDc); Changed(); FillProfileEditor(); });
        undoLink.Margin = new Thickness(10, 3, 0, 0);
        void ShowCustom() { var custom = Templates.IsCustom(profile, true, App.Engine.NotSupported) || Templates.IsCustom(profile, false, App.Engine.NotSupported); customPill.Visibility = undoLink.Visibility = custom ? Visibility.Visible : Visibility.Collapsed; }
        title.Children.Add(customPill); title.Children.Add(undoLink); ShowCustom();
        var grid = new Grid { Margin = new Thickness(0, 2, 0, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) }); grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) }); grid.ColumnDefinitions.Add(new ColumnDefinition());
        foreach (var pluggedIn in new[] { true, false })
        {
            var row = grid.RowDefinitions.Count; grid.RowDefinitions.Add(new RowDefinition());
            var position = pluggedIn ? profile.SpeedAc : profile.SpeedDc;
            var label = Text(pluggedIn ? "🔌 Plugged in" : "🔋 On battery", 12.5, false, quietBrush, new Thickness(0, 6, 0, 0));
            var leaveBox = new CheckBox { Content = "Set it", IsChecked = position != null, Margin = new Thickness(0, 6, 0, 0) };
            var slider = new Slider { Minimum = 0, Maximum = 4, TickFrequency = 1, IsSnapToTickEnabled = true, TickPlacement = TickPlacement.BottomRight, Value = position ?? 2, IsEnabled = position != null, Margin = new Thickness(0, 4, 0, 0) };
            var ends = new Grid(); foreach (var (labelText, alignment) in new[] { (Catalog.SpeedLabels[0], HorizontalAlignment.Left), (Catalog.SpeedLabels[2], HorizontalAlignment.Center), (Catalog.SpeedLabels[4], HorizontalAlignment.Right) }) ends.Children.Add(new TextBlock { Text = labelText, FontSize = 11, Foreground = quietBrush, HorizontalAlignment = alignment });
            var sliderStack = new StackPanel { Children = { slider, ends }, Margin = new Thickness(0, 0, 0, 10) };
            leaveBox.Click += (sender, args) => { slider.IsEnabled = leaveBox.IsChecked == true; Templates.ApplySpeed(profile, pluggedIn, leaveBox.IsChecked == true ? (int)slider.Value : null); if (leaveBox.IsChecked != true) foreach (var key in Catalog.SpeedPresets.Keys) if (profile.Values.TryGetValue(key, out var pair)) { if (pluggedIn) pair.Ac = null; else pair.Dc = null; } Changed(); FillProfileEditor(); };
            slider.ValueChanged += (sender, args) => { if (!slider.IsEnabled) return; Templates.ApplySpeed(profile, pluggedIn, (int)slider.Value); Changed(); ShowCustom(); };
            slider.PreviewMouseUp += (sender, args) => FillProfileEditor();
            Grid.SetRow(label, row); Grid.SetRow(leaveBox, row); Grid.SetColumn(leaveBox, 1); Grid.SetRow(sliderStack, row); Grid.SetColumn(sliderStack, 2);
            grid.Children.Add(label); grid.Children.Add(leaveBox); grid.Children.Add(sliderStack);
        }
        body.Children.Add(grid);
    }

    // Label, plugged-in choice, on-battery choice. "Leave (now …)" keeps Windows' value
    Grid RowsGrid(Profile profile, List<SettingDef> rows, bool onFace)
    {
        var grid = new Grid { Margin = new Thickness(0, onFace ? 0 : 6, 0, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(240) }); grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MaxWidth = 260 }); grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MaxWidth = 260 });
        void Add(UIElement element, int row, int column, int span = 1) { Grid.SetRow(element, row); Grid.SetColumn(element, column); Grid.SetColumnSpan(element, span); grid.Children.Add(element); }
        if (rows.Any(row => row.TwoSided))
        {
            grid.RowDefinitions.Add(new RowDefinition());
            Add(Text("🔌 Plugged in", 11.5, false, quietBrush, new Thickness(2, 0, 0, 2)), 0, 1); Add(Text("🔋 On battery", 11.5, false, quietBrush, new Thickness(10, 0, 0, 2)), 0, 2);
        }
        foreach (var setting in rows)
        {
            var row = grid.RowDefinitions.Count; grid.RowDefinitions.Add(new RowDefinition());
            var isCustomRow = !onFace && Catalog.SpeedPresets.ContainsKey(setting.Key) && RowDiffersFromDial(profile, setting.Key);
            Add(new StackPanel { Orientation = Orientation.Horizontal, Children = { new Border { Width = 7, Height = 7, CornerRadius = new CornerRadius(3.5), Background = new SolidColorBrush(Color.FromRgb(0xE0, 0x91, 0x2F)), Margin = new Thickness(0, 0, 6, 0), Visibility = isCustomRow ? Visibility.Visible : Visibility.Collapsed }, Text(setting.Label, 13, false, null, new Thickness(0, 7, 0, 0)) } }, row, 0);
            Add(Choice(profile, setting, true), row, 1, setting.TwoSided ? 1 : 2);
            if (setting.TwoSided) Add(Choice(profile, setting, false), row, 2);
        }
        return grid;
    }

    bool RowDiffersFromDial(Profile profile, string key)
    {
        var pair = profile.Get(key); if (pair == null) return false;
        return (profile.SpeedAc is { } ac && pair.Ac != Catalog.SpeedPresets[key][ac]) || (profile.SpeedDc is { } dc && pair.Dc != Catalog.SpeedPresets[key][dc]);
    }

    ComboBox Choice(Profile profile, SettingDef setting, bool pluggedIn)
    {
        var options = setting.Kind == SettingKind.Refresh ? new List<Option> { new(-1, "Lowest"), new(-2, "Highest") }.Concat(Direct.RefreshRates().Select(hertz => new Option(hertz, $"{hertz} Hz"))).ToList() : setting.Options.ToList();
        var current = profile.Get(setting.Key) is { } pair ? (pluggedIn ? pair.Ac : pair.Dc) : null;
        if (current != null && options.All(option => option.Value != current)) options.Add(new Option(current.Value, setting.Describe(current.Value)));
        var combo = new ComboBox { Margin = new Thickness(pluggedIn ? 0 : 10, 3, 0, 3), MaxDropDownHeight = 320 };
        // What "not set" means here: Starting profile's value, or (for brightness, volume, radios, and inside tweaks) left alone
        var starting = Settings.Profiles.FirstOrDefault(candidate => candidate.IsStarting);
        var fallback = starting != null && starting != profile && profile is not Tweak && !Catalog.YoursToChange.Contains(setting.Key) && starting.Get(setting.Key) is { } startingPair && (pluggedIn || !setting.TwoSided ? startingPair.Ac : startingPair.Dc) is { } startingValue ? startingValue : (int?)null;
        var notSetLabel = fallback is { } value ? $"As Starting profile ({(setting.Kind == SettingKind.Refresh ? $"{value} Hz" : setting.Describe(value))})" : profile is Tweak ? "As the profile has it" : "Leave as it is";
        combo.Items.Add(new ComboBoxItem { Content = notSetLabel, Tag = null, Foreground = quietBrush });
        foreach (var option in options) combo.Items.Add(new ComboBoxItem { Content = option.Label, Tag = option.Value });
        combo.SelectedItem = combo.Items.Cast<ComboBoxItem>().FirstOrDefault(item => item.Tag is int value && value == current) ?? combo.Items[0];
        combo.SelectionChanged += (sender, args) =>
        {
            var chosen = (combo.SelectedItem as ComboBoxItem)?.Tag as int?;
            if (!profile.Values.TryGetValue(setting.Key, out var target)) profile.Values[setting.Key] = target = new SidePair();
            if (pluggedIn) target.Ac = chosen; else target.Dc = chosen;
            Changed();
            if (Catalog.SpeedPresets.ContainsKey(setting.Key)) Dispatcher.BeginInvoke(profile is Tweak ? FillTweakEditor : FillProfileEditor);
        };
        return combo;
    }

    // ---- Tweaks (SPEC.md › Tweaks) ----
    void FillTweaks() { FillTweakList(); FillTweakEditor(); }
    void FillTweakList()
    {
        tweakList.Children.Clear();
        if (Settings.Tweaks.All(tweak => tweak.Id != selectedTweakId)) selectedTweakId = Settings.Tweaks.FirstOrDefault()?.Id ?? "";
        foreach (var tweak in Settings.Tweaks)
        {
            var on = App.Engine.TweakIsOn(tweak);
            var card = new Border { Style = (Style)FindResource("Card"), Margin = new Thickness(0, 0, 0, 8), Padding = new Thickness(12, 10, 12, 10), Cursor = Cursors.Hand, Child = new StackPanel { Children = { new StackPanel { Orientation = Orientation.Horizontal, Children = { Dot(tweak.Colour), Text(tweak.Name + (on ? "  · on now" : ""), 13.5, true) } }, Text((tweak.Scheduled ? $"{tweak.From} to {tweak.To}. " : "By hand or remote. ") + Describe(tweak), 12, false, quietBrush, new Thickness(0, 4, 0, 0)) } } };
            if (tweak.Id == selectedTweakId) { card.BorderBrush = accentBrush; card.BorderThickness = new Thickness(2); }
            card.MouseLeftButtonUp += (sender, args) => { selectedTweakId = tweak.Id; FillTweaks(); };
            tweakList.Children.Add(card);
        }
        tweakList.Children.Add(Link("＋ New tweak", () => { var tweak = new Tweak { Name = Settings.Tweaks.Count == 0 ? "Night" : "New tweak", Colour = "#5B5FC7" }; Settings.Tweaks.Add(tweak); selectedTweakId = tweak.Id; Settings.Save(); FillTweaks(); }));
    }
    void FillTweakEditor()
    {
        tweakEditor.Children.Clear();
        var tweak = Settings.Tweaks.FirstOrDefault(candidate => candidate.Id == selectedTweakId);
        if (tweak == null) { tweakEditor.Children.Add(Callout("A tweak is a few settings laid over whichever profile is on, e.g. Night: screen off after 2 minutes, volume down. It turns on by a schedule or from the tray.")); return; }
        var nameBox = new TextBox { Text = tweak.Name, FontSize = 17, FontWeight = FontWeights.SemiBold, MinWidth = 240, Padding = new Thickness(4, 2, 4, 2) };
        nameBox.TextChanged += (sender, args) => { if (nameBox.Text.Trim() != "") { tweak.Name = nameBox.Text.Trim(); Changed(); } };
        var delete = Button("Delete", () => { Settings.Tweaks.Remove(tweak); Settings.Power.TweakOverrides.Remove(tweak.Id); selectedTweakId = ""; Changed(); FillTweaks(); }); delete.Margin = new Thickness(0); DockPanel.SetDock(delete, Dock.Right);
        tweakEditor.Children.Add(new DockPanel { Margin = new Thickness(0, 0, 0, 12), Children = { delete, new StackPanel { Orientation = Orientation.Horizontal, Children = { nameBox } } } });
        // When: schedule
        var scheduled = new CheckBox { Content = "On a schedule", IsChecked = tweak.Scheduled, Margin = new Thickness(0) };
        var fromBox = new TextBox { Text = tweak.From, Width = 60, Margin = new Thickness(8, 0, 6, 0) }; var toBox = new TextBox { Text = tweak.To, Width = 60, Margin = new Thickness(6, 0, 0, 0) };
        scheduled.Click += (sender, args) => { tweak.Scheduled = scheduled.IsChecked == true; Changed(); };
        fromBox.LostFocus += (sender, args) => { if (TimeSpan.TryParse(fromBox.Text, out var time)) { tweak.From = time.ToString(@"hh\:mm"); Changed(); } fromBox.Text = tweak.From; };
        toBox.LostFocus += (sender, args) => { if (TimeSpan.TryParse(toBox.Text, out var time)) { tweak.To = time.ToString(@"hh\:mm"); Changed(); } toBox.Text = tweak.To; };
        var volume = new ComboBox { MinWidth = 160, Margin = new Thickness(8, 0, 0, 0) };
        foreach (var (label, change) in new (string, int?)[] { ("Leave it", null), ("Down 10%", -10), ("Down 20%", -20), ("Down 30%", -30), ("Down 50%", -50), ("Up 10%", 10), ("Up 20%", 20) }) volume.Items.Add(new ComboBoxItem { Content = label, Tag = change });
        volume.SelectedItem = volume.Items.Cast<ComboBoxItem>().FirstOrDefault(item => (int?)item.Tag == tweak.VolumeChange) ?? volume.Items[0];
        volume.SelectionChanged += (sender, args) => { tweak.VolumeChange = (volume.SelectedItem as ComboBoxItem)?.Tag as int?; Changed(); };
        tweakEditor.Children.Add(Card(new StackPanel { Children = { Text("When", 14.5, true, null, new Thickness(0, 0, 0, 8)), new StackPanel { Orientation = Orientation.Horizontal, Children = { scheduled, Text("from", 13, false, quietBrush, new Thickness(12, 2, 0, 0)), fromBox, Text("to", 13, false, quietBrush, new Thickness(0, 2, 0, 0)), toBox } }, Text("Or tick it in the tray menu: it holds until the next scheduled change.", 12, false, quietBrush, new Thickness(0, 8, 0, 0)), new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0), Children = { Text("Volume", 13, false, null, new Thickness(0, 3, 0, 0)), volume } } } }));
        AddDials(tweakEditor, tweak);
    }

    // ---- Automatic switching: the rules (SPEC.md › Situations) ----
    StackPanel? learnPanel;
    Inventory? learnBefore;
    // Rules read as sentences: "When the Home dock is connected, use Desk"; the first that matches wins; Otherwise is last. Adding is a row of tiles (Mat 17 Nov)
    void FillSituations()
    {
        situationsPage.Children.Clear();
        situationsPage.Children.Add(Callout("WinSwitch picks a profile by itself from what's happening. Each rule says: when this happens, use that profile. The first rule that matches wins, so put the most specific ones at the top. A profile picked from the tray menu stays on until something here changes."));
        var current = App.Engine.SituationId; var present = App.Engine.Current.Things;
        var greenBrush = new SolidColorBrush(Color.FromRgb(0x1D, 0x6B, 0x35));
        for (var index = 0; index < Settings.Situations.Count; index++)
        {
            var situation = Settings.Situations[index]; var position = index;
            var glyph = situation.Kind switch { "battery" => "\uE850", "network" => "\uE701", _ => "\uE7F4" };
            var when = situation.Kind switch { "battery" => "When I'm on battery and it's under", "network" => "When I'm on the network", _ => "When this is connected:" };
            var sentence = new WrapPanel { VerticalAlignment = VerticalAlignment.Center };
            sentence.Children.Add(Text(when, 13.5, false, null, new Thickness(0, 4, 8, 4)));
            if (situation.Kind == "battery")
            {
                var below = new ComboBox { MinWidth = 70, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
                foreach (var level in new[] { 10, 15, 20, 25, 30, 40, 50 }) below.Items.Add(new ComboBoxItem { Content = $"{level}%", Tag = level });
                below.SelectedItem = below.Items.Cast<ComboBoxItem>().FirstOrDefault(item => (int)item.Tag == situation.BatteryBelow) ?? below.Items[2];
                below.SelectionChanged += (sender, args) => { situation.BatteryBelow = (int)((ComboBoxItem)below.SelectedItem).Tag; Changed(); };
                sentence.Children.Add(below);
            }
            else
            {
                var nameBox = new TextBox { Text = situation.Name, MinWidth = 150, FontWeight = FontWeights.SemiBold, Padding = new Thickness(4, 2, 4, 2), Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center, ToolTip = "Its name; click to rename" };
                nameBox.LostFocus += (sender, args) => { if (nameBox.Text.Trim() != "" && nameBox.Text.Trim() != situation.Name) { situation.Name = nameBox.Text.Trim(); Changed(); } };
                sentence.Children.Add(nameBox);
            }
            sentence.Children.Add(Text("use", 13.5, false, null, new Thickness(0, 4, 8, 4)));
            sentence.Children.Add(ProfilePicker(situation.ProfileId, id => { situation.ProfileId = id; Changed(); }));
            // Small icon tools on the right: up, down, delete
            var tools = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Top };
            tools.Children.Add(IconTool("\uE70E", "Move up", position > 0, () => { Settings.Situations.RemoveAt(position); Settings.Situations.Insert(position - 1, situation); Changed(); FillSituations(); }));
            tools.Children.Add(IconTool("\uE70D", "Move down", position < Settings.Situations.Count - 1, () => { Settings.Situations.RemoveAt(position); Settings.Situations.Insert(position + 1, situation); Changed(); FillSituations(); }));
            tools.Children.Add(IconTool("\uE74D", "Delete this rule", true, () => { Settings.Situations.Remove(situation); Changed(); FillSituations(); }));
            var icon = new Border { Width = 32, Height = 32, CornerRadius = new CornerRadius(8), Background = new SolidColorBrush(Color.FromRgb(0xEE, 0xF4, 0xFC)), Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Top, Child = new TextBlock { Text = glyph, FontFamily = new FontFamily("Segoe MDL2 Assets"), Foreground = accentBrush, FontSize = 15, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } };
            var body = new StackPanel { Children = { sentence } };
            if (Engine.CoveredBy(Settings.Situations, situation) is { } coveredBy) body.Children.Add(new Border { Style = (Style)FindResource("Warning"), Margin = new Thickness(0, 8, 0, 0), Child = Text($"Never used: the rule above for {Engine.Words(coveredBy)} always matches first. Delete this one, or move it up to make it win.", 12.5, false, new SolidColorBrush(Color.FromRgb(0x6B, 0x45, 0x00))) });
            if (situation.Id == current) body.Children.Add(new Border { Background = new SolidColorBrush(Color.FromRgb(0xE3, 0xF4, 0xE8)), CornerRadius = new CornerRadius(9), Padding = new Thickness(8, 1, 8, 2), Margin = new Thickness(0, 6, 0, 0), HorizontalAlignment = HorizontalAlignment.Left, Child = Text("Matching now", 11, true, greenBrush) });
            if (situation.Things.Count > 0)
            {
                var list = new StackPanel(); foreach (var (thing, name) in situation.Things) list.Children.Add(Text((present.ContainsKey(thing) ? "● " : "○ ") + name + (present.ContainsKey(thing) ? "" : "  (not connected now)"), 12, false, quietBrush, new Thickness(0, 2, 0, 0)));
                body.Children.Add(new Expander { Header = situation.Kind == "dock" ? $"How WinSwitch recognises it ({situation.Things.Count} things; most of them must be connected)" : "How WinSwitch recognises it", Style = (Style)FindResource("More"), Content = list });
            }
            var row = new DockPanel { Children = { icon, tools, body } }; DockPanel.SetDock(icon, Dock.Left); DockPanel.SetDock(tools, Dock.Right);
            var card = Card(row); if (situation.Id == current) { card.BorderBrush = new SolidColorBrush(Color.FromRgb(0x9F, 0xD6, 0xB0)); card.BorderThickness = new Thickness(1.5); }
            situationsPage.Children.Add(card);
        }
        // Otherwise
        var otherwise = new WrapPanel(); otherwise.Children.Add(Text(Settings.Situations.Count == 0 ? "Always use" : "Otherwise, use", 13.5, true, null, new Thickness(0, 4, 8, 4))); otherwise.Children.Add(ProfilePicker(Settings.DefaultProfileId, id => { Settings.DefaultProfileId = id; Changed(); }));
        var otherwiseCard = Card(new StackPanel { Children = { otherwise, current == "default" ? new Border { Background = new SolidColorBrush(Color.FromRgb(0xE3, 0xF4, 0xE8)), CornerRadius = new CornerRadius(9), Padding = new Thickness(8, 1, 8, 2), Margin = new Thickness(0, 6, 0, 0), HorizontalAlignment = HorizontalAlignment.Left, Child = Text("Matching now", 11, true, greenBrush) } : new Border() } });
        situationsPage.Children.Add(otherwiseCard);
        // Add a rule: tiles that say what each one does
        situationsPage.Children.Add(Text("Add a rule", 14.5, true, null, new Thickness(0, 14, 0, 8)));
        var tiles = new WrapPanel();
        var inventory = App.Engine.Current; var docked = Engine.DockLike(inventory.Things);
        tiles.Children.Add(Tile("\uE7F4", "A dock, monitor or device", "Unplug it, plug it back in, and WinSwitch learns what it brings.", StartLearning));
        bool Exists(string kind, IEnumerable<string> keys) => Settings.Situations.Any(situation => situation.Kind == kind && situation.Things.Keys.ToHashSet().SetEquals(keys));
        if (docked.Count > 0 && !Exists("dock", docked.Keys)) tiles.Children.Add(Tile("\uE7F4", "What's plugged in now", string.Join(", ", docked.Values.Distinct().Take(2)) + ". No unplugging needed.", () => AddSituation(new Situation { Kind = "dock", Name = docked.Values.First() + " dock", Things = docked })));
        if (inventory.NetworkThing != "" && !Exists("network", new[] { inventory.NetworkThing })) tiles.Children.Add(Tile("\uE701", "This network", $"{inventory.NetworkName}, recognised by its router.", () => AddSituation(new Situation { Kind = "network", Name = inventory.NetworkName, Things = new() { [inventory.NetworkThing] = inventory.NetworkName } })));
        var lowestBattery = Settings.Situations.Where(situation => situation.Kind == "battery").Select(situation => situation.BatteryBelow).DefaultIfEmpty(30).Min();
        if (PowerApi.PowerStatus().hasBattery && lowestBattery > 10) { var level = lowestBattery == 30 && !Settings.Situations.Any(situation => situation.Kind == "battery") ? 20 : Math.Max(10, lowestBattery - 10); tiles.Children.Add(Tile("\uE850", "Battery running low", $"On battery and under {level}% (you can change the level).", () => AddSituation(new Situation { Kind = "battery", Name = "Battery low", BatteryBelow = level }))); }
        situationsPage.Children.Add(tiles);
        if (learnPanel != null) situationsPage.Children.Add(learnPanel);
    }

    static Border IconTool(string glyph, string tip, bool enabled, Action action)
    {
        var tool = new Border { Width = 28, Height = 28, CornerRadius = new CornerRadius(6), Background = Brushes.Transparent, Cursor = enabled ? Cursors.Hand : Cursors.Arrow, ToolTip = tip, Opacity = enabled ? 1 : 0.3, Child = new TextBlock { Text = glyph, FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 12, Foreground = quietBrush, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } };
        if (!enabled) return tool;
        tool.MouseEnter += (sender, args) => tool.Background = new SolidColorBrush(Color.FromRgb(0xEE, 0xF1, 0xF5)); tool.MouseLeave += (sender, args) => tool.Background = Brushes.Transparent;
        tool.MouseLeftButtonUp += (sender, args) => { args.Handled = true; action(); };
        return tool;
    }

    Border Tile(string glyph, string title, string detail, Action action)
    {
        var tile = new Border { Width = 250, Background = Brushes.White, BorderBrush = lineBrush, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Padding = new Thickness(14, 12, 14, 12), Margin = new Thickness(0, 0, 10, 10), Cursor = Cursors.Hand,
            Child = new StackPanel { Children = { new StackPanel { Orientation = Orientation.Horizontal, Children = { new TextBlock { Text = glyph, FontFamily = new FontFamily("Segoe MDL2 Assets"), Foreground = accentBrush, FontSize = 14, Margin = new Thickness(0, 2, 8, 0) }, Text("＋ " + title, 13.5, true) } }, Text(detail, 12, false, quietBrush, new Thickness(0, 4, 0, 0)) } } };
        tile.MouseEnter += (sender, args) => { tile.BorderBrush = accentBrush; tile.Background = new SolidColorBrush(Color.FromRgb(0xF7, 0xFA, 0xFE)); };
        tile.MouseLeave += (sender, args) => { tile.BorderBrush = lineBrush; tile.Background = Brushes.White; };
        tile.MouseLeftButtonUp += (sender, args) => action();
        return tile;
    }

    void AddSituation(Situation situation) { situation.ProfileId = App.Engine.ActiveProfile?.Id ?? Settings.DefaultProfileId; Settings.Situations.Insert(0, situation); Changed(); FillSituations(); }

    ComboBox ProfilePicker(string selectedId, Action<string> chosen)
    {
        var combo = new ComboBox { MinWidth = 200 };
        foreach (var profile in Settings.Profiles) combo.Items.Add(new ComboBoxItem { Content = new StackPanel { Orientation = Orientation.Horizontal, Children = { Dot(profile.Colour), new TextBlock { Text = profile.Name } } }, Tag = profile.Id });
        combo.SelectedItem = combo.Items.Cast<ComboBoxItem>().FirstOrDefault(item => (string)item.Tag == selectedId);
        combo.SelectionChanged += (sender, args) => { if (combo.SelectedItem is ComboBoxItem { Tag: string id }) chosen(id); };
        return combo;
    }

    // Learn what I plug in: unplug, then plug in; whatever appears is the dock (SPEC.md › Situations)
    void StartLearning()
    {
        learnBefore = null;
        learnPanel = new StackPanel();
        learnPanel.Children.Add(Callout("1. Unplug the dock (or device) now, wait a moment, then press Ready."));
        learnPanel.Children.Add(new WrapPanel { Margin = new Thickness(0, 8, 0, 0), Children = { Button("Ready", async () => { App.Engine.EvaluateSoon(true); await Task.Delay(2500); learnBefore = App.Engine.Current; App.Engine.ThingsAppeared -= OnThingsAppeared; App.Engine.ThingsAppeared += OnThingsAppeared; learnPanel!.Children.Clear(); learnPanel.Children.Add(Callout("2. Now plug it in. WinSwitch is watching…")); learnPanel.Children.Add(Button("Cancel", () => { App.Engine.ThingsAppeared -= OnThingsAppeared; learnPanel = null; FillSituations(); })); }, true), Button("Cancel", () => { learnPanel = null; FillSituations(); }) } });
        FillSituations();
    }
    void OnThingsAppeared(Inventory after, Dictionary<string, string> appeared) => Dispatcher.BeginInvoke(() =>
    {
        if (learnPanel == null) return;
        App.Engine.ThingsAppeared -= OnThingsAppeared;
        var things = appeared.Where(thing => learnBefore == null || !learnBefore.Things.ContainsKey(thing.Key)).ToDictionary(thing => thing.Key, thing => thing.Value);
        if (things.Count == 0) { learnPanel.Children.Clear(); learnPanel.Children.Add(Callout("Nothing new appeared. Try again, or plug it into another port.", true)); learnPanel.Children.Add(Button("Try again", StartLearning)); return; }
        var nameBox = new TextBox { Text = Engine.DockLike(things).Values.FirstOrDefault() is { } monitor ? monitor + " dock" : "My dock", MinWidth = 200 };
        var picker = ProfilePicker(App.Engine.ActiveProfile?.Id ?? Settings.DefaultProfileId, id => { });
        learnPanel.Children.Clear();
        var list = new StackPanel(); foreach (var name in things.Values) list.Children.Add(Text("● " + name, 12, false, quietBrush, new Thickness(0, 2, 0, 0)));
        learnPanel.Children.Add(Card(new StackPanel { Children = { Text("It brought:", 13, true), list, new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0), Children = { Text("Name", 13, false, null, new Thickness(0, 3, 8, 0)), nameBox, Text("use", 13, false, quietBrush, new Thickness(10, 3, 8, 0)), picker } }, new WrapPanel { Margin = new Thickness(0, 10, 0, 0), Children = { Button("Save", () => { Settings.Situations.Insert(0, new Situation { Kind = "dock", Name = nameBox.Text.Trim() == "" ? "Dock" : nameBox.Text.Trim(), Things = things, ProfileId = (picker.SelectedItem as ComboBoxItem)?.Tag as string ?? Settings.DefaultProfileId }); learnPanel = null; Changed(); FillSituations(); }, true), Button("Cancel", () => { learnPanel = null; FillSituations(); }) } } } }));
    });

    // ---- Original settings (SPEC.md › Original settings) ----
    async Task FillOriginalAsync()
    {
        originalPage.Children.Clear();
        var snapshot = App.Snapshot;
        if (snapshot == null) { originalPage.Children.Add(Text("Not read yet.", 13, false, quietBrush)); return; }
        originalPage.Children.Add(Callout($"Saved {snapshot.TakenAt:d MMMM yyyy, HH:mm}, before WinSwitch changed anything. Power plan then: {(snapshot.PlanName == "" ? snapshot.Plan : snapshot.PlanName)}. Uninstalling WinSwitch puts all of this back."));
        var buttons = new WrapPanel { Margin = new Thickness(0, 10, 0, 12) };
        if (Settings.Power.Paused) buttons.Children.Add(Button("Resume WinSwitch", () => { App.Engine.Resume(); _ = FillOriginalAsync(); }, true));
        else buttons.Children.Add(Button("Restore everything", async () => { if (MessageBox.Show(this, "Put every setting back as it was before WinSwitch? WinSwitch pauses until you resume it.", "WinSwitch", MessageBoxButton.OKCancel) == MessageBoxResult.OK) { await App.RestoreEverythingAsync(); await FillOriginalAsync(); } }, true));
        buttons.Children.Add(Button("Everything that was captured…", () => new CaptureWindow(snapshot) { Owner = this }.Show()));
        originalPage.Children.Add(buttons);
        var plan = Guid.TryParse(Settings.Power.OwnPlan, out var own) ? own : PowerApi.ActiveScheme() ?? Guid.Empty;
        var now = await Reader.ReadAllAsync(Settings.Power.Paused ? PowerApi.ActiveScheme() ?? plan : plan);
        var grid = new Grid();
        foreach (var width in new[] { 260.0, 200, 200, 110 }) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(width) });
        void Cell(UIElement element, int row, int column) { Grid.SetRow(element, row); Grid.SetColumn(element, column); grid.Children.Add(element); }
        grid.RowDefinitions.Add(new RowDefinition()); Cell(Text("Setting", 11.5, true, quietBrush), 0, 0); Cell(Text("Original", 11.5, true, quietBrush), 0, 1); Cell(Text("Now", 11.5, true, quietBrush), 0, 2);
        string Show(SettingDef setting, SidePair? pair) => pair == null ? "" : setting.TwoSided && pair.Ac != pair.Dc ? $"{Value(setting, pair.Ac)} / {Value(setting, pair.Dc)}" : Value(setting, pair.Ac ?? pair.Dc);
        static string Value(SettingDef setting, int? value) => value == null ? "?" : setting.Kind == SettingKind.Refresh ? $"{value} Hz" : setting.Describe(value.Value);
        foreach (var setting in Catalog.All.Where(setting => snapshot.Values.ContainsKey(setting.Key)))
        {
            var row = grid.RowDefinitions.Count; grid.RowDefinitions.Add(new RowDefinition());
            var original = snapshot.Values[setting.Key]; var current = now.GetValueOrDefault(setting.Key);
            var differs = current != null && (current.Ac != original.Ac || setting.TwoSided && current.Dc != original.Dc);
            Cell(Text(setting.Label + (setting.TwoSided ? "" : ""), 12.5, false, null, new Thickness(0, 5, 8, 5)), row, 0);
            Cell(Text(Show(setting, original), 12.5, false, null, new Thickness(0, 5, 8, 5)), row, 1);
            Cell(Text(Show(setting, current), 12.5, differs, differs ? new SolidColorBrush(Color.FromRgb(0x9A, 0x5A, 0x00)) : quietBrush, new Thickness(0, 5, 8, 5)), row, 2);
            if (differs) { var restore = Button("Restore", () => { _ = RestoreOneAsync(setting, original); }); restore.Padding = new Thickness(8, 1, 8, 1); restore.Margin = new Thickness(0, 2, 0, 2); Cell(restore, row, 3); }
            var line = new Border { BorderBrush = lineBrush, BorderThickness = new Thickness(0, 0, 0, 1) }; Grid.SetColumnSpan(line, 4); Cell(line, row, 0);
        }
        originalPage.Children.Add(Card(new StackPanel { Children = { Text("Plugged in / on battery where they differ", 11.5, false, quietBrush, new Thickness(0, 0, 0, 6)), grid } }));
        if (snapshot.Devices.Count > 0)
        {
            var devices = new StackPanel();
            foreach (var device in snapshot.Devices.Where(device => device.MayWake != null || device.MayTurnOff != null).Take(60)) devices.Children.Add(Text($"{device.Name}:  {(device.MayTurnOff == true ? "may be turned off to save power" : device.MayTurnOff == false ? "always on" : "")}{(device.MayWake == true ? ", may wake the PC" : "")}", 12, false, quietBrush, new Thickness(0, 2, 0, 0)));
            originalPage.Children.Add(Card(new Expander { Header = $"Device power settings ({snapshot.Devices.Count}, saved for restoring; changing them comes with the helper)", Style = (Style)FindResource("More"), Margin = new Thickness(0), Content = devices }));
        }
    }

    // Puts one setting back and stops profiles changing it, so it stays as it was
    async Task RestoreOneAsync(SettingDef setting, SidePair original)
    {
        foreach (var profile in Settings.Profiles.Where(profile => !profile.IsStarting).Concat(Settings.Tweaks)) { profile.Values.Remove(setting.Key); if (Catalog.SpeedPresets.ContainsKey(setting.Key)) { profile.SpeedAc = null; profile.SpeedDc = null; } }
        Settings.Power.LastWritten.Remove(setting.Key);
        if (setting.Kind == SettingKind.Plan && Guid.TryParse(Settings.Power.OwnPlan, out var plan)) { Writer.WritePlan(plan, setting, original); PowerApi.SetActiveScheme(plan); }
        else await Writer.WriteDirect(setting, original, PowerApi.PowerStatus().pluggedIn);
        Changed(); FillProfiles(); await FillOriginalAsync();
    }

    // ---- General ----
    void WireGeneralTab()
    {
        startWithWindowsBox.IsChecked = Settings.General.StartWithWindows; startWithWindowsBox.IsEnabled = !AppPaths.IsPortable;
        startWithWindowsBox.Click += (sender, args) => { Settings.General.StartWithWindows = startWithWindowsBox.IsChecked == true; Settings.Save(); try { StartupRegistration.Apply(Settings.General.StartWithWindows); } catch { } };
        notificationsCombo.SelectedItem = notificationsCombo.Items.Cast<ComboBoxItem>().FirstOrDefault(item => (string)item.Tag == Settings.General.Notifications) ?? notificationsCombo.Items[0];
        notificationsCombo.SelectionChanged += (sender, args) => { if (notificationsCombo.SelectedItem is ComboBoxItem { Tag: string mode }) { Settings.General.Notifications = mode; Settings.Save(); } };
        askBeforeClosingBox.IsChecked = Settings.General.AskBeforeClosing;
        askBeforeClosingBox.Click += (sender, args) => { Settings.General.AskBeforeClosing = askBeforeClosingBox.IsChecked == true; Settings.Save(); };
        void ShowStopped() { var names = Settings.Power.Stopped.Select(app => app.Name).ToList(); stoppedText.Text = names.Count == 0 ? "Nothing is stopped now." : "Stopped now: " + string.Join(", ", names); startStoppedButton.IsEnabled = names.Count > 0; }
        startStoppedButton.Click += async (sender, args) => { await App.Apps.StartTheseAgainAsync(Settings.Power.Stopped.ToList()); ShowStopped(); };
        ShowStopped(); App.Engine.Changed += () => Dispatcher.BeginInvoke(ShowStopped);
        fitWindowsCombo.SelectedItem = fitWindowsCombo.Items.Cast<ComboBoxItem>().FirstOrDefault(item => (string)item.Tag == Settings.General.FitWindows) ?? fitWindowsCombo.Items[0];
        fitWindowsCombo.SelectionChanged += (sender, args) => { if (fitWindowsCombo.SelectedItem is ComboBoxItem { Tag: string mode }) { Settings.General.FitWindows = mode; Settings.Save(); } };
        fitWindowsButton.Click += (sender, args) => { var fitted = WindowFitter.FitAllNow(); fitWindowsDone.Text = fitted == 0 ? "Every window already fits." : $"Fitted {fitted} window{(fitted == 1 ? "" : "s")}."; };
        wizardButton.Click += (sender, args) => App.OpenWizard();
        quitButton.Click += (sender, args) => App.Quit();
    }

    // ---- About (as the sibling apps) ----
    void WireAboutTab()
    {
        versionText.Text = $"WinSwitch {App.Updater.CurrentVersion}";
        dataFolderText.Text = (AppPaths.IsPortable ? "Portable mode. " : "") + $"Settings, original settings and logs: {AppPaths.DataFolder}";
        openLogsButton.Click += (sender, args) => Process.Start(new ProcessStartInfo(AppPaths.LogsFolder) { UseShellExecute = true });
        copyDiagnosticsButton.Click += (sender, args) => { try { Clipboard.SetText(DiagnosticsReport()); copyDiagnosticsDone.Text = "Copied"; } catch (Exception error) { copyDiagnosticsDone.Text = "Couldn't copy: " + error.Message; } };
        issuesLink.Click += (sender, args) => OpenUrl(AppUpdater.RepoUrl + "/issues");
        channelCombo.SelectedItem = channelCombo.Items.Cast<ComboBoxItem>().First(item => (string)item.Tag == Settings.General.UpdateChannel);
        channelCombo.SelectionChanged += async (sender, args) =>
        {
            if (channelCombo.SelectedItem is not ComboBoxItem chosen) return;
            Settings.General.UpdateChannel = (string)chosen.Tag; Settings.Save();
            await LoadVersionsAsync(); await App.Updater.CheckNowAsync();
        };
        autoUpdateBox.IsChecked = Settings.General.AutoUpdate;
        autoUpdateBox.Click += (sender, args) => { Settings.General.AutoUpdate = autoUpdateBox.IsChecked == true; Settings.Save(); };
        checkNowButton.Click += async (sender, args) => await App.Updater.CheckNowAsync();
        restartToUpdateButton.Click += async (sender, args) => await App.Updater.RestartToUpdateAsync();
        refreshVersionsButton.Click += async (sender, args) => await LoadVersionsAsync();
        versionsList.SelectionChanged += (sender, args) => ShowSelectedVersion();
        switchVersionButton.Click += async (sender, args) => { if (versionsList.SelectedItem is VersionItem item) await App.Updater.SwitchToAsync(item.Release); };
        App.Updater.Changed += ShowUpdateStatus;
        Closed += (sender, args) => App.Updater.Changed -= ShowUpdateStatus;
        ShowUpdateStatus();
        _ = LoadVersionsAsync();
    }

    record VersionItem(ReleaseEntry Release, bool IsCurrent)
    {
        public string Label => $"{Release.Version}{(Release.IsDev ? "  dev" : "")}  ·  {Release.Published:d MMM yyyy HH:mm}";
        public Visibility CurrentVisibility => IsCurrent ? Visibility.Visible : Visibility.Collapsed;
        public override string ToString() => Label + (IsCurrent ? "  ·  running now" : "");
    }
    async Task LoadVersionsAsync()
    {
        try
        {
            var releases = await App.Updater.ListReleasesAsync(Settings.General.UpdateChannel == "dev");
            versionsList.ItemsSource = releases.Select(release => new VersionItem(release, release.Version == App.Updater.CurrentVersion)).ToList();
            if (releases.Count == 0) versionNotesText.Text = "No versions published yet.";
        }
        catch (Exception listError) { versionNotesText.Text = $"Could not load versions: {listError.Message}"; }
    }
    void ShowSelectedVersion()
    {
        var selected = versionsList.SelectedItem as VersionItem;
        switchVersionButton.IsEnabled = selected != null && !selected.IsCurrent && App.Updater.IsInstalled;
        versionNotesText.Text = selected == null ? "" : (selected.Release.Notes.Length > 600 ? selected.Release.Notes[..600] + "…" : selected.Release.Notes);
    }
    void ShowUpdateStatus() => Dispatcher.BeginInvoke(() =>
    {
        updateStatusText.Text = App.Updater.StatusText;
        restartToUpdateButton.Visibility = App.Updater.ReadyVersion == "" ? Visibility.Collapsed : Visibility.Visible;
        restartToUpdateButton.Content = $"Restart to update to {App.Updater.ReadyVersion}";
    });

    // Version, Windows, power state and what's connected (names only), plus the end of the crash log
    string DiagnosticsReport()
    {
        var (pluggedIn, percent, hasBattery) = PowerApi.PowerStatus();
        var lines = new List<string> { $"WinSwitch {App.Updater.CurrentVersion} ({(AppPaths.IsPortable ? "portable" : "installed")}, {Settings.General.UpdateChannel})", $"Windows {Environment.OSVersion.Version}", $"Power: {(pluggedIn ? "plugged in" : "on battery")}{(hasBattery ? $", {percent}%" : ", no battery")}, hibernate {(PowerApi.CanHibernate() ? "available" : "not available")}" };
        lines.Add($"Plan: own {(Settings.Power.OwnPlan == "" ? "none" : "yes")}, writes to original: {Settings.Power.WritesToOriginal}, paused: {Settings.Power.Paused}, active: {PowerApi.SchemeName(PowerApi.ActiveScheme() ?? Guid.Empty)}");
        lines.Add($"Profiles: {Settings.Profiles.Count}, tweaks: {Settings.Tweaks.Count}, situations: {Settings.Situations.Count}, on now: {App.Engine.ActiveProfile?.Name} ({App.Engine.Reason})");
        lines.Add($"Not supported here: {string.Join(", ", App.Engine.NotSupported)}");
        lines.Add($"Connected: {string.Join("; ", App.Engine.Current.Things.Values.Distinct().Take(40))}");
        try { var crashLog = System.IO.File.ReadAllText(CrashReporter.LogPath); lines.Add("--- crash.log (end) ---"); lines.Add(crashLog.Length > 4000 ? crashLog[^4000..] : crashLog); } catch { }
        return string.Join(Environment.NewLine, lines);
    }
    static void OpenUrl(string url) => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
}
