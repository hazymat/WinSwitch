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

// The visual profile page (Mat 17 Nov, mockup 4): one Plugged in / On battery switch, a laptop preview that dims with Brightness,
// sliders with labelled stops, lid picture tiles, toggle tiles. "What this sets" still opens every detailed setting
public partial class SettingsWindow
{
    bool editingPluggedIn = PowerApi.PowerStatus().pluggedIn;
    static readonly Brush greenBrush = new SolidColorBrush(Color.FromRgb(0x2E, 0xA0, 0x5A));

    void AddVisualDials(Panel page, Profile profile)
    {
        var notSupported = App.Engine.NotSupported;
        bool Available(string key) => Catalog.Find(key) is { } setting && !notSupported.Contains(key) && (App.Snapshot?.Values.ContainsKey(setting.Key) ?? true);
        var side = editingPluggedIn;
        // Which side the page shows
        var hasBattery = PowerApi.PowerStatus().hasBattery;
        if (hasBattery)
        {
            var switcher = new Border { Background = new SolidColorBrush(Color.FromRgb(0xE9, 0xEC, 0xF0)), CornerRadius = new CornerRadius(18), Padding = new Thickness(3), HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, -4, 0, 10) };
            var both = new StackPanel { Orientation = Orientation.Horizontal };
            foreach (var (pluggedIn, label) in new[] { (true, "  Plugged in"), (false, "  On battery") })
            {
                var on = pluggedIn == side;
                var option = new Border { CornerRadius = new CornerRadius(15), Padding = new Thickness(14, 5, 14, 6), Cursor = Cursors.Hand, Background = on ? Brushes.White : Brushes.Transparent, Effect = on ? new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 4, ShadowDepth = 1, Opacity = 0.15 } : null, Child = new TextBlock { FontWeight = FontWeights.SemiBold, Foreground = on ? Brushes.Black : quietBrush, Inlines = { new System.Windows.Documents.Run(label[..1]) { FontFamily = new FontFamily("Segoe MDL2 Assets") }, new System.Windows.Documents.Run(label[1..]) } } };
                option.MouseLeftButtonUp += (sender, args) => { editingPluggedIn = pluggedIn; FillProfileEditor(); };
                both.Children.Add(option);
            }
            switcher.Child = both;
            page.Children.Add(new DockPanel { Children = { switcher, Text(side ? "Showing what happens while it's plugged in." : "Showing what happens on battery.", 12.5, false, quietBrush, new Thickness(0, 4, 0, 0)) } });
            DockPanel.SetDock(switcher, Dock.Right);
        }
        // ---- Screen ----
        {
            var body = DialCard(page, "", Catalog.Screen, profile);
            var preview = LaptopPreview(out var dim);
            var controls = new StackPanel { Margin = new Thickness(22, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            if (Available("brightness"))
            {
                var current = Value(profile, "brightness", side);
                dim.Opacity = DimFor(current ?? Direct.Brightness() ?? 80);
                controls.Children.Add(SetItRow(profile, "brightness", side, "", "Brightness", "Set the brightness", Enumerable.Range(0, 21).Select(step => step * 5).ToArray(), value => $"{value}%", (current ?? Direct.Brightness() ?? 80) / 5 * 5, moving => dim.Opacity = DimFor(moving)));
            }
            else dim.Opacity = 0;
            if (Available("refresh") && Direct.RefreshRates() is { Count: > 1 } rates) controls.Children.Add(ChipsRow(profile, "refresh", side, "Refresh rate", rates.Select(hertz => (hertz, $"{hertz} Hz" + (hertz == rates[0] ? ": longer battery" : hertz == rates[^1] ? ": smoother" : ""))).ToArray()));
            var row = new DockPanel { Children = { preview, controls } }; DockPanel.SetDock(preview, Dock.Left);
            body.Children.Add(row);
            AddMore(body, profile, Catalog.Screen, new[] { "brightness", "screenOff", "refresh", "dimAfter", "dimmedBrightness", "blankAfter", "lockAfter" });
        }
        // ---- When I step away ----
        {
            var body = DialCard(page, "\uE916", "When I step away", profile);
            body.Children.Add(StepAway(profile, side, Available, hasBattery));
        }
        // ---- Sleep and lid ----
        {
            var body = DialCard(page, "", Catalog.Sleep, profile);
            if (Available("lid") && hasBattery)
            {
                body.Children.Add(Text("When I close the lid", 12.5, false, null, new Thickness(0, 0, 0, 6)));
                body.Children.Add(Tiles(profile, "lid", side, new[] { (0, "", "Keep running", "Segoe MDL2 Assets"), (1, "", "Sleep", "Segoe MDL2 Assets"), (2, "❄", "Hibernate", "Segoe UI Symbol"), (3, "", "Shut down", "Segoe MDL2 Assets") }));
            }
            AddMore(body, profile, Catalog.Sleep, new[] { "lid", "sleepAfter", "hibernateAfter", "signInOnWake" });
        }
        // ---- Battery or speed ----
        {
            var body = DialCard(page, "", Catalog.Speed, profile);
            AddSpeed(body, profile, side);
            AddMore(body, profile, Catalog.Speed, Array.Empty<string>());
        }
        // ---- Wireless and devices ----
        {
            var body = DialCard(page, "", Catalog.Wireless, profile);
            var tiles = new UniformGrid { Columns = 4 };
            if (Available("wifi")) tiles.Children.Add(ToggleTile(profile, "wifi", side, "", "Wi-Fi", 1, 0, "On", "Off"));
            if (Available("bluetooth")) tiles.Children.Add(ToggleTile(profile, "bluetooth", side, "", "Bluetooth", 1, 0, "On", "Off"));
            if (Available("mobile")) tiles.Children.Add(ToggleTile(profile, "mobile", side, "", "Mobile data", 1, 0, "On", "Off"));
            if (Available("graphics")) tiles.Children.Add(ToggleTile(profile, "graphics", side, "", "Graphics", 1, 2, "Power-saving chip", "Full performance"));
            if (Available("videoPlayback")) tiles.Children.Add(ToggleTile(profile, "videoPlayback", side, "", "Video", 2, 0, "Tuned for battery", "Best picture"));
            body.Children.Add(tiles);
            AddMore(body, profile, Catalog.Wireless, new[] { "wifi", "bluetooth", "mobile", "graphics", "videoPlayback", "wakeInput" });
        }
        // ---- Sound ----
        if (Available("volume"))
        {
            var body = DialCard(page, "", Catalog.Sound, profile);
            body.Children.Add(SetItRow(profile, "volume", side, "", "Volume", "Set the volume", Enumerable.Range(0, 21).Select(step => step * 5).ToArray(), value => $"{value}%", (Value(profile, "volume", side) ?? Direct.Volume() ?? 30) / 5 * 5, null));
            body.Children.Add(OutputRow(profile));
        }
    }

    // The step-away line for the side shown: Windows' own timers plus WinSwitch's Blank and Lock. Any change writes the whole line for this side
    FrameworkElement StepAway(Profile profile, bool side, Func<string, bool> available, bool hasBattery)
    {
        var stages = new List<IdleTimeline.Stage>();
        void Stage(string key, string name, string note, Color fill, Color ink, bool show) { if (show) stages.Add(new IdleTimeline.Stage(key, name, note, fill, ink)); }
        Stage("dimAfter", "Dim", "", Color.FromRgb(0xF4, 0xC4, 0x6A), Color.FromRgb(0x4A, 0x33, 0x00), available("dimAfter"));
        Stage("blankAfter", "Blank", "black, instant back", Color.FromRgb(0x2B, 0x2F, 0x36), Color.FromRgb(0xE9, 0xEC, 0xF0), true);
        Stage("screenOff", "Screen off", "back in a second", Color.FromRgb(0x0B, 0x0C, 0x0E), Color.FromRgb(0xC9, 0xCE, 0xD6), available("screenOff"));
        Stage("sleepAfter", "Sleep", "back in a few seconds", Color.FromRgb(0x3B, 0x3F, 0x8F), Color.FromRgb(0xE6, 0xE8, 0xFF), available("sleepAfter"));
        Stage("hibernateAfter", "Hibernate", "uses no battery", Color.FromRgb(0x1E, 0x3A, 0x5F), Color.FromRgb(0xDB, 0xE9, 0xFF), available("hibernateAfter") && hasBattery);
        int Shown(string key) => Catalog.AsTime(Value(profile, key, side) ?? Fallback(profile, key, side)) ?? 0;
        // Windows refused "Sign in after waking: Not needed" (it wants admin rights), so it still asks for a password when the PC sleeps; on modern standby that's when the screen goes off (Mat 18 Nov)
        var windowsLocksAt = App.Engine.NotSupported.Contains("signInOnWake") && PowerApi.ActiveScheme() is { } activePlan && Reader.ReadPlan(activePlan, Catalog.Find("signInOnWake")!, side) == 1 ? PowerApi.ModernStandby() ? "screenOff" : "sleepAfter" : "";
        var timeline = new IdleTimeline(stages, stages.ToDictionary(stage => stage.Key, stage => Shown(stage.Key)), Shown("lockAfter"), Value(profile, "dimmedBrightness", side) ?? Fallback(profile, "dimmedBrightness", side) ?? 30) { Margin = new Thickness(0, 4, 0, 0), WindowsLocksAt = windowsLocksAt };
        timeline.Committed += () =>
        {
            foreach (var stage in stages) SetValue(profile, stage.Key, side, timeline.SecondsOf(stage.Key));
            SetValue(profile, "lockAfter", side, timeline.SecondsOf("lock"));
            if (available("dimmedBrightness")) SetValue(profile, "dimmedBrightness", side, timeline.DimmedBrightness);
        };
        var panel = new StackPanel();
        panel.Children.Add(Text("Drag the edges to change times. Drag a stage off the line to stop using it.", 12.5, false, quietBrush, new Thickness(0, -6, 0, 6)));
        panel.Children.Add(timeline);
        if (windowsLocksAt != "") panel.Children.Add(new WrapPanel { Margin = new Thickness(0, 4, 0, 0), Children =
        {
            // Never Windows' own "If you've been away": Windows Hello greys it out (Mat 19 Nov)
            Text(!HelperClient.Installed ? "Windows only lets WinSwitch change this with the helper (one admin prompt)." : HelperClient.NeedsUpdate(HelperClient.InstalledVersion) ? "Your helper is too old to change this. Updating it takes one admin prompt." : "A policy on this PC (often a work or school one) insists on it, so Lock can only come before Screen off.", 12.5, false, quietBrush, new Thickness(0, 3, 8, 4)),
            !HelperClient.Installed || HelperClient.NeedsUpdate(HelperClient.InstalledVersion) ? Button(HelperClient.Installed ? "Update the helper" : "Install the helper", async () => { await HelperClient.InstallAsync(); FillProfiles(); }, primary: true) : new TextBlock(),
        } });
        return panel;
    }

    // Sound output: a tick, then the outputs plugged in now (plus the chosen one if it isn't); one choice for both power sources
    FrameworkElement OutputRow(Profile profile)
    {
        var outputs = Direct.Outputs();
        if (profile.SoundOutput != "" && !outputs.Any(output => output.id == profile.SoundOutput)) outputs.Add((profile.SoundOutput, $"{profile.SoundOutputName} (not plugged in)"));
        var row = new StackPanel { Margin = new Thickness(0, 0, 0, 4) };
        var tick = new CheckBox { Content = "Switch the sound output", IsChecked = profile.SoundOutput != "", Margin = new Thickness(0, 0, 0, 4) };
        var picker = new ComboBox { ItemsSource = outputs.Select(output => output.name).ToList(), Margin = new Thickness(22, 0, 0, 0), MaxWidth = 380, HorizontalAlignment = HorizontalAlignment.Left, Visibility = profile.SoundOutput != "" ? Visibility.Visible : Visibility.Collapsed };
        picker.SelectedIndex = Math.Max(0, outputs.FindIndex(output => output.id == (profile.SoundOutput != "" ? profile.SoundOutput : Direct.OutputId())));
        void Choose() { if (picker.SelectedIndex < 0 || picker.SelectedIndex >= outputs.Count) return; profile.SoundOutput = outputs[picker.SelectedIndex].id; profile.SoundOutputName = outputs[picker.SelectedIndex].name.Replace(" (not plugged in)", ""); Changed(); }
        picker.SelectionChanged += (sender, args) => { if (tick.IsChecked == true) Choose(); };
        var note = Text("Left as you have it.", 12, false, quietBrush, new Thickness(22, 0, 0, 0));
        note.Visibility = profile.SoundOutput == "" ? Visibility.Visible : Visibility.Collapsed;
        tick.Click += (sender, args) =>
        {
            var on = tick.IsChecked == true;
            picker.Visibility = on ? Visibility.Visible : Visibility.Collapsed; note.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
            if (on) Choose(); else { profile.SoundOutput = ""; profile.SoundOutputName = ""; Changed(); }
        };
        if (outputs.Count == 0) { tick.IsEnabled = false; note.Text = "No sound outputs found."; }
        row.Children.Add(tick); row.Children.Add(picker); row.Children.Add(note);
        return row;
    }

    static double DimFor(int brightness) => (100 - Math.Clamp(brightness, 0, 100)) / 100.0 * 0.8;
    static string ShortTime(int seconds) => seconds == 0 ? "Never" : seconds < 3600 ? $"{seconds / 60}m" : $"{seconds / 3600}h";

    // ---- Values for the side being shown; one-sided settings keep theirs in Ac ----
    static int? Value(Profile profile, string key, bool pluggedIn) { var setting = Catalog.Find(key); var pair = profile.Get(key); return pair == null ? null : pluggedIn || setting?.TwoSided != true ? pair.Ac : pair.Dc; }
    void SetValue(Profile profile, string key, bool pluggedIn, int? value)
    {
        var setting = Catalog.Find(key);
        if (!profile.Values.TryGetValue(key, out var pair)) profile.Values[key] = pair = new SidePair();
        if (pluggedIn || setting?.TwoSided != true) pair.Ac = value; else pair.Dc = value;
        Changed();
    }
    // What "not set" means for this row: Starting profile's value (null when it's yours to change, or this is Starting profile)
    int? Fallback(Profile profile, string key, bool pluggedIn) { var starting = Settings.Profiles.FirstOrDefault(candidate => candidate.IsStarting); return starting == null || starting == profile || profile is Tweak || Catalog.YoursToChange.Contains(key) ? null : Value(starting, key, pluggedIn); }

    // ---- Building blocks ----
    StackPanel DialCard(Panel page, string glyph, string title, Profile profile)
    {
        var body = new StackPanel();
        var head = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 12) };
        head.Children.Add(new Border { Width = 28, Height = 28, CornerRadius = new CornerRadius(7), Background = new SolidColorBrush(Color.FromRgb(0xEE, 0xF4, 0xFC)), Margin = new Thickness(0, 0, 10, 0), Child = new TextBlock { Text = glyph, FontFamily = new FontFamily("Segoe MDL2 Assets"), Foreground = accentBrush, FontSize = 14, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } });
        head.Children.Add(Text(title, 15, true, null, new Thickness(0, 3, 0, 0)));
        body.Children.Add(head);
        var card = Card(body); card.CornerRadius = new CornerRadius(10); card.Padding = new Thickness(16, 14, 16, 14);
        page.Children.Add(card);
        return body;
    }

    // Every detailed setting of the dial that isn't on its face, both sides, one click away
    void AddMore(StackPanel body, Profile profile, string dial, string[] onFace)
    {
        var notSupported = App.Engine.NotSupported;
        var rest = Catalog.All.Where(setting => setting.Dial == dial && !onFace.Contains(setting.Key) && !notSupported.Contains(setting.Key) && (App.Snapshot?.Values.ContainsKey(setting.Key) ?? true)).ToList();
        if (rest.Count > 0) body.Children.Add(new Expander { Header = $"What this sets ({rest.Count})", Style = (Style)FindResource("More"), Margin = new Thickness(0, 12, 0, 0), Content = RowsGrid(profile, rest, false) });
    }

    // A drawn laptop whose screen dims with the brightness
    static FrameworkElement LaptopPreview(out Border dim)
    {
        dim = new Border { Background = Brushes.Black, Opacity = 0.2, CornerRadius = new CornerRadius(3) };
        var wallpaper = new Grid { ClipToBounds = true, Children = { new Border { CornerRadius = new CornerRadius(3), Background = new LinearGradientBrush(Color.FromRgb(0x5A, 0xA0, 0xE8), Color.FromRgb(0xE8, 0x8C, 0x5A), 35) }, new Border { Width = 70, Height = 44, Background = new SolidColorBrush(Color.FromArgb(0xD9, 0xFF, 0xFF, 0xFF)), CornerRadius = new CornerRadius(3), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(12, 12, 0, 0) }, new Border { Width = 80, Height = 46, Background = new SolidColorBrush(Color.FromArgb(0xF0, 0xFF, 0xFF, 0xFF)), CornerRadius = new CornerRadius(3), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(52, 32, 0, 0) }, dim } };
        var lid = new Border { Width = 176, Height = 110, CornerRadius = new CornerRadius(9, 9, 2, 2), Background = new SolidColorBrush(Color.FromRgb(0x1C, 0x1F, 0x25)), Padding = new Thickness(7), Child = wallpaper };
        var keyboardBase = new Border { Width = 200, Height = 9, CornerRadius = new CornerRadius(0, 0, 8, 8), Background = new LinearGradientBrush(Color.FromRgb(0xC6, 0xCC, 0xD4), Color.FromRgb(0x9A, 0xA2, 0xAD), 90) };
        return new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children = { lid, keyboardBase } };
    }

    // Label and value line above a control: "☾ Screen off after ........ 5 minutes"
    DockPanel LabelLine(string glyph, string label, out TextBlock valueText)
    {
        valueText = new TextBlock { FontWeight = FontWeights.SemiBold, FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center };
        var left = new TextBlock { FontSize = 12.5, Foreground = new SolidColorBrush(Color.FromRgb(0x44, 0x48, 0x50)) };
        if (glyph != "") left.Inlines.Add(new System.Windows.Documents.Run(glyph + "  ") { FontFamily = new FontFamily(glyph.Length == 1 && glyph[0] < 0xE000 ? "Segoe UI Symbol" : "Segoe MDL2 Assets"), Foreground = quietBrush });
        left.Inlines.Add(label);
        var line = new DockPanel { Margin = new Thickness(0, 0, 0, 2) };
        DockPanel.SetDock(valueText, Dock.Right); line.Children.Add(valueText); line.Children.Add(left);
        return line;
    }

    // A slider over fixed stops (times); not set = Starting profile's value, shown greyed, with ↺ to go back to it
    FrameworkElement StopsRow(Profile profile, string key, bool side, string glyph, string label, int[] stops, Func<int, string> stopLabel)
    {
        var setting = Catalog.Find(key)!;
        var value = Catalog.AsTime(Value(profile, key, side)); var fallback = Catalog.AsTime(Fallback(profile, key, side));
        var shown = value ?? fallback;
        if (shown is { } extra && !stops.Contains(extra)) stops = stops.Where(stop => stop != 0).Append(extra).OrderBy(stop => stop).Concat(stops.Contains(0) ? new[] { 0 } : Array.Empty<int>()).Distinct().ToArray();
        var row = new StackPanel { Margin = new Thickness(0, 0, 0, 14) };
        var line = LabelLine(glyph, label, out var valueText);
        string Words(int seconds) => setting.Describe(seconds);
        void ShowValue(int? set) { valueText.Text = set is { } chosen ? Words(chosen) : fallback is { } starting ? $"As Starting profile ({Words(starting)})" : "Not changed"; valueText.Foreground = set == null ? quietBrush : Brushes.Black; }
        ShowValue(value);
        if (value != null && fallback != null) { var reset = new TextBlock { Text = "↺", Foreground = accentBrush, Cursor = Cursors.Hand, FontSize = 13, Margin = new Thickness(8, 0, 0, 0), ToolTip = "Back to Starting profile's" }; reset.MouseLeftButtonUp += (sender, args) => { SetValue(profile, key, side, null); FillProfileEditor(); }; DockPanel.SetDock(reset, Dock.Right); line.Children.Insert(0, reset); }
        var slider = new NiceSlider(stops.Length, shown is { } index ? Array.IndexOf(stops, index) : stops.Length / 2) { Dimmed = value == null };
        slider.Moving += position => ShowValue(stops[position]);
        slider.Chosen += position => { SetValue(profile, key, side, stops[position]); slider.Dimmed = false; };
        var labels = new UniformGrid { Rows = 1, Columns = stops.Length, Margin = new Thickness(0, 0, 0, 0) };
        foreach (var stop in stops) labels.Children.Add(new TextBlock { Text = stopLabel(stop), FontSize = 10.5, Foreground = stop == shown ? accentBrush : quietBrush, FontWeight = stop == shown ? FontWeights.SemiBold : FontWeights.Normal, HorizontalAlignment = HorizontalAlignment.Center });
        row.Children.Add(line); row.Children.Add(slider); row.Children.Add(labels);
        return row;
    }

    // "Set the volume" (or brightness): the slider only shows when ticked; unticked leaves it as you have it
    FrameworkElement SetItRow(Profile profile, string key, bool side, string glyph, string label, string tickText, int[] stops, Func<int, string> words, int startAt, Action<int>? moving)
    {
        var value = Value(profile, key, side);
        var row = new StackPanel { Margin = new Thickness(0, 0, 0, 14) };
        var tick = new CheckBox { Content = tickText, IsChecked = value != null, Margin = new Thickness(0, 0, 0, 4) };
        var line = LabelLine(glyph, label, out var valueText);
        var current = value ?? startAt;
        valueText.Text = words(current);
        var slider = new NiceSlider(stops.Length, Array.IndexOf(stops, stops.OrderBy(stop => Math.Abs(stop - current)).First()));
        slider.Moving += position => { valueText.Text = words(stops[position]); moving?.Invoke(stops[position]); };
        slider.Chosen += position => SetValue(profile, key, side, stops[position]);
        var sliderPanel = new StackPanel { Children = { line, slider }, Visibility = value != null ? Visibility.Visible : Visibility.Collapsed };
        tick.Click += (sender, args) => { var on = tick.IsChecked == true; sliderPanel.Visibility = on ? Visibility.Visible : Visibility.Collapsed; SetValue(profile, key, side, on ? stops[slider.Index] : null); if (on) moving?.Invoke(stops[slider.Index]); };
        row.Children.Add(tick); row.Children.Add(sliderPanel);
        if (value == null) row.Children.Add(Text("Left as you have it.", 12, false, quietBrush, new Thickness(22, 0, 0, 0)));
        return row;
    }

    // Small chips (refresh rate), plus "As Starting profile"
    FrameworkElement ChipsRow(Profile profile, string key, bool side, string label, (int value, string text)[] options)
    {
        var value = Value(profile, key, side); var fallback = Fallback(profile, key, side);
        var row = new StackPanel { Margin = new Thickness(0, 0, 0, 4) };
        row.Children.Add(Text(label, 12.5, false, new SolidColorBrush(Color.FromRgb(0x44, 0x48, 0x50)), new Thickness(0, 0, 0, 5)));
        var chips = new WrapPanel();
        void Chip(string text, bool on, Action pick)
        {
            var chip = new Border { CornerRadius = new CornerRadius(14), Padding = new Thickness(12, 4, 12, 5), Margin = new Thickness(0, 0, 6, 6), Cursor = Cursors.Hand, Background = on ? accentBrush : Brushes.White, BorderBrush = on ? accentBrush : new SolidColorBrush(Color.FromRgb(0xD6, 0xDA, 0xE0)), BorderThickness = new Thickness(1), Child = new TextBlock { Text = text, FontSize = 12, Foreground = on ? Brushes.White : Brushes.Black, FontWeight = on ? FontWeights.SemiBold : FontWeights.Normal } };
            chip.MouseLeftButtonUp += (sender, args) => { pick(); FillProfileEditor(); };
            chips.Children.Add(chip);
        }
        foreach (var (optionValue, text) in options) Chip(text, value == optionValue, () => SetValue(profile, key, side, optionValue));
        Chip(fallback is { } starting ? $"As Starting profile ({starting} Hz)" : "Leave as it is", value == null, () => SetValue(profile, key, side, null));
        row.Children.Add(chips);
        return row;
    }

    // Picture tiles for one choice (the lid)
    FrameworkElement Tiles(Profile profile, string key, bool side, (int value, string glyph, string text, string font)[] options)
    {
        var value = Value(profile, key, side) ?? Fallback(profile, key, side); var isSet = Value(profile, key, side) != null;
        var grid = new UniformGrid { Columns = options.Length, Margin = new Thickness(0, 0, 0, 14) };
        foreach (var (optionValue, glyph, text, font) in options)
        {
            var on = optionValue == value;
            var tile = new Border { CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1.5), Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(6, 10, 6, 9), Cursor = Cursors.Hand, Background = on ? new SolidColorBrush(Color.FromRgb(0xEE, 0xF4, 0xFC)) : Brushes.White, BorderBrush = on ? (isSet ? accentBrush : new SolidColorBrush(Color.FromRgb(0x9F, 0xBF, 0xE6))) : lineBrush,
                Child = new StackPanel { Children = { new TextBlock { Text = glyph, FontFamily = new FontFamily(font), FontSize = 22, Foreground = on ? accentBrush : quietBrush, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 4) }, new TextBlock { Text = text, FontSize = 12, FontWeight = on ? FontWeights.SemiBold : FontWeights.Normal, HorizontalAlignment = HorizontalAlignment.Center, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap }, new TextBlock { Text = on && !isSet ? "As Starting profile" : "", FontSize = 10.5, Foreground = quietBrush, HorizontalAlignment = HorizontalAlignment.Center } } } };
            tile.MouseLeftButtonUp += (sender, args) => { SetValue(profile, key, side, optionValue); FillProfileEditor(); };
            grid.Children.Add(tile);
        }
        return grid;
    }

    // A toggle tile: on and off are two values; dashed while not set (Starting profile's, or left as you have it), with × to go back to that
    FrameworkElement ToggleTile(Profile profile, string key, bool side, string glyph, string title, int onValue, int offValue, string onText, string offText)
    {
        var value = Value(profile, key, side); var fallback = Fallback(profile, key, side);
        var shown = value ?? fallback; var on = shown == onValue;
        var toggle = new Border { Width = 36, Height = 20, CornerRadius = new CornerRadius(10), Background = on ? greenBrush : new SolidColorBrush(Color.FromRgb(0xCF, 0xD4, 0xDA)), Opacity = value == null ? 0.55 : 1, Child = new Border { Width = 16, Height = 16, CornerRadius = new CornerRadius(8), Background = Brushes.White, HorizontalAlignment = on ? HorizontalAlignment.Right : HorizontalAlignment.Left, Margin = new Thickness(2, 0, 2, 0) } };
        var top = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
        DockPanel.SetDock(toggle, Dock.Right); top.Children.Add(toggle);
        top.Children.Add(new TextBlock { Text = glyph, FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 18, Foreground = accentBrush });
        var subtitle = value == null ? fallback is { } ? $"As Starting profile: {(on ? onText : offText).ToLowerInvariant()}" : "Left as you have it" : on ? onText : offText;
        var content = new StackPanel { Children = { top, Text(title, 12.5, true), Text(subtitle, 11, false, quietBrush, new Thickness(0, 1, 0, 0)) } };
        var tile = new Border { CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1.5), Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(12, 10, 12, 10), Cursor = Cursors.Hand, Background = Brushes.White, BorderBrush = value == null ? new SolidColorBrush(Color.FromRgb(0xD3, 0xD8, 0xDF)) : lineBrush, Child = content };
        if (value == null) tile.BorderBrush = new VisualBrush { Viewport = new Rect(0, 0, 8, 8), ViewportUnits = BrushMappingMode.Absolute, TileMode = TileMode.Tile, Visual = new Border { Width = 8, Height = 8, Background = new LinearGradientBrush(new GradientStopCollection { new(Color.FromRgb(0xC5, 0xCB, 0xD3), 0), new(Color.FromRgb(0xC5, 0xCB, 0xD3), 0.5), new(Colors.Transparent, 0.5), new(Colors.Transparent, 1) }, 0) } };
        else { var clear = new TextBlock { Text = "×", Foreground = quietBrush, FontSize = 13, Cursor = Cursors.Hand, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, -2, 0, 0), ToolTip = fallback == null ? "Leave it as you have it" : "Back to Starting profile's" }; clear.MouseLeftButtonUp += (sender, args) => { args.Handled = true; SetValue(profile, key, side, null); FillProfileEditor(); }; content.Children.Add(clear); }
        tile.MouseLeftButtonUp += (sender, args) => { if (args.Handled) return; SetValue(profile, key, side, on ? offValue : onValue); FillProfileEditor(); };
        return tile;
    }

    // Battery or speed for the side shown: one slider over five presets; a row changed by hand marks it Custom
    void AddSpeed(StackPanel body, Profile profile, bool side)
    {
        var position = side ? profile.SpeedAc : profile.SpeedDc;
        var custom = position != null && Templates.IsCustom(profile, side, App.Engine.NotSupported);
        var line = LabelLine("", position == null ? "Not set: each row below follows Starting profile" : "", out var valueText);
        valueText.Text = position is { } chosen ? Catalog.SpeedLabels[chosen] + (custom ? "  ·  Custom" : "") : "";
        valueText.Foreground = custom ? new SolidColorBrush(Color.FromRgb(0x9A, 0x5A, 0x00)) : greenBrush;
        var gradient = new LinearGradientBrush(new GradientStopCollection { new(Color.FromRgb(0x3F, 0xAE, 0x68), 0), new(Color.FromRgb(0x2F, 0x7F, 0xD8), 0.5), new(Color.FromRgb(0xE0, 0x81, 0x2F), 1) }, 0);
        var slider = new NiceSlider(5, position ?? 2, gradient) { Dimmed = position == null };
        slider.Moving += index => { valueText.Text = Catalog.SpeedLabels[index]; valueText.Foreground = greenBrush; };
        slider.Chosen += index => { Templates.ApplySpeed(profile, side, index); Changed(); FillProfileEditor(); };
        var ends = new UniformGrid { Rows = 1, Columns = 5 };
        for (var index = 0; index < 5; index++) ends.Children.Add(new TextBlock { Text = (index == 0 ? "🍃 " : index == 4 ? "🚀 " : "") + Catalog.SpeedLabels[index], FontSize = 11.5, Foreground = index == position ? greenBrush : quietBrush, FontWeight = index == position ? FontWeights.SemiBold : FontWeights.Normal, HorizontalAlignment = index == 0 ? HorizontalAlignment.Left : index == 4 ? HorizontalAlignment.Right : HorizontalAlignment.Center });
        body.Children.Add(line); body.Children.Add(slider); body.Children.Add(ends);
        var tools = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        if (custom) tools.Children.Add(Link("Undo custom", () => { Templates.ApplySpeed(profile, side, position); Changed(); FillProfileEditor(); }));
        if (position != null) { var clear = Link("Don't set (follow Starting profile)", () => { Templates.ApplySpeed(profile, side, null); foreach (var key in Catalog.SpeedPresets.Keys) if (profile.Values.TryGetValue(key, out var pair)) { if (side) pair.Ac = null; else pair.Dc = null; } Changed(); FillProfileEditor(); }); clear.Margin = new Thickness(custom ? 16 : 0, 6, 0, 0); tools.Children.Add(clear); }
        if (tools.Children.Count > 0) body.Children.Add(tools);
    }
}
