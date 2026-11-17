using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using WinSwitch.Power;
using WinSwitch.Rules;
using WinSwitch.Settings;
namespace WinSwitch;

// A few questions, one per page, big buttons, nothing technical; ends with the profiles it will make as sentences (SPEC.md › First run)
public sealed class WizardWindow : Window
{
    App App => App.Instance;
    AppSettings Settings => App.Settings;
    readonly StackPanel page = new() { Margin = new Thickness(36, 28, 36, 20) };
    readonly DockPanel footer = new() { Margin = new Thickness(36, 0, 36, 24), LastChildFill = false };
    static readonly Brush accent = new SolidColorBrush(Color.FromRgb(0x2F, 0x7F, 0xD8)), quiet = new SolidColorBrush(Color.FromRgb(0x5A, 0x62, 0x70));
    int step;
    // Answers, pre-picked from what WinSwitch can see
    string machine = PowerApi.PowerStatus().hasBattery ? "laptop" : "desktop";
    string where = "both";
    bool dockIsHome = true;
    int dayMinutes = 30, nightMinutes = 2, batteryPriority = 0, lidAway = 0, lidAwayPlugged = 1;
    string night = "22:00-07:00";
    int? nightVolume = -20;
    bool replace = true;
    readonly Dictionary<string, string> dockNow = Engine.DockLike(App.Instance.Engine.Current.Things);

    public WizardWindow()
    {
        Title = "Set up WinSwitch"; Width = 640; Height = 600; WindowStartupLocation = WindowStartupLocation.CenterScreen; ResizeMode = ResizeMode.CanMinimize;
        Background = new SolidColorBrush(Color.FromRgb(0xF7, 0xF8, 0xFA)); FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");
        try { Icon = System.Windows.Media.Imaging.BitmapFrame.Create(new Uri("pack://application:,,,/Assets/WinSwitch.ico")); } catch { }
        var root = new DockPanel(); DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer); root.Children.Add(new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = page });
        Content = root;
        KeyDown += (sender, args) => { if (args.Key == Key.Escape) Close(); };
        if (machine == "laptop" && dockNow.Count > 0) where = "both";
        Render();
    }

    List<string> Steps() => machine == "laptop" ? new() { "welcome", "machine", "where", "screen", "night", "volume", "battery", "lid", "summary" } : new() { "welcome", "machine", "screen", "night", "volume", "summary" };

    void Render()
    {
        page.Children.Clear(); footer.Children.Clear();
        var steps = Steps(); step = Math.Clamp(step, 0, steps.Count - 1);
        switch (steps[step])
        {
            case "welcome":
                Heading("Let's set up WinSwitch", "I saved your current settings as Starting profile, so you can always go back. A few quick questions and I'll suggest profiles for how you use this PC.");
                break;
            case "machine":
                Heading("What is this?");
                Choice(new[] { ("laptop", "Laptop", "Runs on battery sometimes"), ("desktop", "Desktop", "Always plugged in"), ("server", "Always-on server", "Should never sleep") }, machine, value => machine = value);
                break;
            case "where":
                Heading("Where do you use it?");
                Choice(new[] { ("dock", "At a desk on a dock", "Mostly plugged in at home"), ("away", "Out and about", "Mostly on battery"), ("both", "Both", "Docked at home, on battery elsewhere") }, where, value => where = value);
                if (where != "away" && dockNow.Count > 0)
                {
                    var box = new CheckBox { Content = $"The dock plugged in now is my home dock ({string.Join(", ", dockNow.Values.Distinct().Take(2))})", IsChecked = dockIsHome, Margin = new Thickness(0, 14, 0, 0), FontSize = 13 };
                    box.Click += (sender, args) => dockIsHome = box.IsChecked == true; page.Children.Add(box);
                }
                else if (where != "away") page.Children.Add(Note("Plug the dock in at any time and WinSwitch will ask what it should use."));
                break;
            case "screen":
                Heading("How long should the screen stay on while you're away from it?", "During the day.");
                Choice(new[] { (5, "5 minutes", ""), (10, "10 minutes", ""), (30, "30 minutes", ""), (60, "1 hour", ""), (0, "Never", "") }, dayMinutes, value => dayMinutes = value, columns: 5);
                page.Children.Add(Note("And at night:", new Thickness(0, 18, 0, 6)));
                Choice(new[] { (1, "1 minute", ""), (2, "2 minutes", ""), (5, "5 minutes", ""), (10, "10 minutes", ""), (-1, "Same as day", "") }, nightMinutes, value => nightMinutes = value, columns: 5);
                break;
            case "night":
                Heading("When is night?");
                Choice(new[] { ("21:00-07:00", "9 pm to 7 am", ""), ("22:00-07:00", "10 pm to 7 am", ""), ("23:00-07:00", "11 pm to 7 am", ""), ("00:00-07:00", "Midnight to 7 am", ""), ("none", "No night settings", "") }, night, value => night = value, columns: 3);
                page.Children.Add(Note("Home Assistant will also be able to switch Night on and off once remote control arrives."));
                break;
            case "volume":
                Heading("Volume at night");
                Choice(new (int?, string, string)[] { (null, "Leave it", ""), (-20, "A bit lower", "Down 20%"), (-40, "Much lower", "Down 40%") }, nightVolume, value => nightVolume = value, columns: 3);
                break;
            case "battery":
                Heading("On battery, what matters more?");
                Choice(new[] { (0, "Longest battery", "Slower, cooler, quieter"), (2, "Balanced", "Windows' usual"), (4, "Fastest", "Full speed, shorter battery") }, batteryPriority, value => batteryPriority = value, columns: 3);
                break;
            case "lid":
                Heading("When you close the lid away from your desk", "At your desk the lid can stay closed with the screens on; this is everywhere else.");
                page.Children.Add(Note("On battery:", new Thickness(0, 0, 0, 6)));
                Choice(new[] { (0, "Sleep, then hibernate after an hour", "Recommended: no flat battery in the bag"), (1, "Sleep", ""), (2, "Hibernate", ""), (3, "Shut down", "") }, lidAway, value => lidAway = value, columns: 2);
                page.Children.Add(Note("Plugged in (charging somewhere else):", new Thickness(0, 18, 0, 6)));
                Choice(new[] { (1, "Sleep", "Recommended"), (0, "Keep running", "e.g. downloads carry on"), (2, "Hibernate", ""), (3, "Shut down", "") }, lidAwayPlugged, value => lidAwayPlugged = value, columns: 4);
                break;
            case "summary":
                Heading("Here's what I'll make", "You can change any of it later in Settings.");
                foreach (var (profile, when) in Plan().profiles) page.Children.Add(Sentence(profile.Colour, $"{profile.Name}" + (when == "" ? "" : $" ({when})"), Describe(profile)));
                if (Plan().tweak is { } tweak) page.Children.Add(Sentence(tweak.Colour, $"Night tweak, {tweak.From} to {tweak.To}", Describe(tweak)));
                if (Settings.Profiles.Any(profile => !profile.IsStarting))
                {
                    var box = new CheckBox { Content = "Replace the profiles I already have (Starting profile stays)", IsChecked = replace, Margin = new Thickness(0, 14, 0, 0) };
                    box.Click += (sender, args) => replace = box.IsChecked == true; page.Children.Add(box);
                }
                break;
        }
        // Footer: Back, Skip / Next or Create
        if (step > 0) footer.Children.Add(Button("Back", () => { step--; Render(); }));
        else footer.Children.Add(Button("Skip, I'll set it up myself", () => { Settings.General.WizardDone = true; Settings.Save(); Close(); App.OpenSettings("Profiles"); }));
        var last = steps[step] == "summary";
        var next = Button(last ? "Create" : step == 0 ? "Start" : "Next", () => { if (last) Create(); else { step++; Render(); } }, primary: true);
        DockPanel.SetDock(next, Dock.Right); footer.Children.Add(next);
    }

    // What the answers make: profiles with the situations that pick them, and the Night tweak
    (List<(Profile profile, string when)> profiles, Tweak? tweak, Dictionary<string, string>? dock) Plan()
    {
        var made = new List<(Profile, string)>();
        Dictionary<string, string>? dock = null;
        if (machine == "server") made.Add((Templates.Server(dayMinutes == 0 ? 30 : dayMinutes), "always"));
        else if (machine == "desktop") { var desk = Templates.Desk(dayMinutes); desk.Name = "Everyday"; desk.Values.Remove("lid"); made.Add((desk, "always")); }
        else
        {
            if (where != "away") { dock = dockIsHome && dockNow.Count > 0 ? dockNow : null; made.Add((Templates.Desk(dayMinutes), dock != null ? "on the home dock" : "when you pick it, or once you've learnt your dock")); }
            made.Add((Templates.OutAndAbout(batteryPriority, lidAway, lidAwayPlugged), where == "away" ? "always" : "everywhere else"));
            made.Add((Templates.Presenting(), "when you pick it"));
        }
        Tweak? tweak = null;
        if (night != "none") { var times = night.Split('-'); tweak = Templates.Night(nightMinutes < 0 ? Math.Max(1, dayMinutes) : nightMinutes, times[0], times[1], nightVolume); if (nightMinutes < 0) tweak.Values.Remove("screenOff"); }
        return (made, tweak, dock);
    }

    void Create()
    {
        var (made, tweak, dock) = Plan();
        if (replace) { Settings.Profiles.RemoveAll(profile => !profile.IsStarting); Settings.Situations.Clear(); Settings.Tweaks.Clear(); Settings.Power.TweakOverrides.Clear(); }
        foreach (var (profile, _) in made) Settings.Profiles.Add(profile);
        var everywhereElse = made.LastOrDefault(entry => entry.when is "everywhere else" or "always").profile ?? made[0].profile;
        Settings.DefaultProfileId = everywhereElse.Id;
        if (dock != null) Settings.Situations.Insert(0, new Situation { Kind = "dock", Name = "Home dock", Things = dock, ProfileId = made[0].profile.Id });
        if (tweak != null) Settings.Tweaks.Add(tweak);
        Settings.Power.HeldProfileId = "";
        Settings.General.WizardDone = true; Settings.Save();
        App.NotifySettingsChanged(); _ = App.Engine.EvaluateAsync(true);
        Close();
        App.OpenSettings("Profiles");
    }

    // ---- Builders ----
    void Heading(string title, string subtitle = "")
    {
        page.Children.Add(new TextBlock { Text = title, FontSize = 22, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, subtitle == "" ? 18 : 6) });
        if (subtitle != "") page.Children.Add(new TextBlock { Text = subtitle, FontSize = 14, Foreground = quiet, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 18) });
    }
    static TextBlock Note(string text, Thickness? margin = null) => new() { Text = text, FontSize = 13, Foreground = quiet, TextWrapping = TextWrapping.Wrap, Margin = margin ?? new Thickness(0, 14, 0, 0) };

    // Big picture-style buttons, one picked
    void Choice<T>(IEnumerable<(T value, string title, string detail)> options, T selected, Action<T> pick, int columns = 1)
    {
        var grid = new System.Windows.Controls.Primitives.UniformGrid { Columns = columns };
        foreach (var (value, title, detail) in options)
        {
            var isPicked = EqualityComparer<T>.Default.Equals(value, selected);
            var content = new StackPanel { Children = { new TextBlock { Text = title, FontSize = 14, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap } } };
            if (detail != "") content.Children.Add(new TextBlock { Text = detail, FontSize = 12, Foreground = quiet, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) });
            var card = new Border { Background = isPicked ? new SolidColorBrush(Color.FromRgb(0xEE, 0xF4, 0xFC)) : Brushes.White, BorderBrush = isPicked ? accent : new SolidColorBrush(Color.FromRgb(0xE3, 0xE5, 0xE8)), BorderThickness = new Thickness(isPicked ? 2 : 1), CornerRadius = new CornerRadius(8), Padding = new Thickness(14, 12, 14, 12), Margin = new Thickness(0, 0, columns > 1 ? 8 : 0, 8), Cursor = Cursors.Hand, Child = content };
            card.MouseLeftButtonUp += (sender, args) => { pick(value); Render(); };
            grid.Children.Add(card);
        }
        page.Children.Add(grid);
    }

    static Border Sentence(string colour, string title, string text) => new()
    {
        Background = Brushes.White, BorderBrush = new SolidColorBrush(Color.FromRgb(0xE3, 0xE5, 0xE8)), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Padding = new Thickness(14, 10, 14, 10), Margin = new Thickness(0, 0, 0, 8),
        Child = new StackPanel { Children = { new StackPanel { Orientation = Orientation.Horizontal, Children = { new Border { Width = 10, Height = 10, CornerRadius = new CornerRadius(5), Background = (Brush)new BrushConverter().ConvertFromString(colour)!, Margin = new Thickness(0, 0, 8, 0) }, new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, FontSize = 14 } } }, new TextBlock { Text = text, Foreground = quiet, FontSize = 12.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 0) } } },
    };

    static string Describe(Profile profile)
    {
        string Time(int seconds) => Catalog.TimeLabel(seconds).Replace(" minutes", " min").Replace(" minute", " min");
        var parts = new List<string>();
        if (profile.Get("screenOff") is { } screen) parts.Add(screen.Ac == 0 ? "screen stays on" : $"screen off after {Time(screen.Ac ?? 0)}" + (screen.Dc != null && screen.Dc != screen.Ac ? $" ({Time(screen.Dc.Value)} on battery)" : ""));
        if (profile.Get("sleepAfter") is { } sleep) parts.Add(sleep.Ac == 0 && sleep.Dc is null or 0 ? "never sleeps" : $"sleeps after {Time(sleep.Ac ?? 0)}" + (sleep.Dc != null && sleep.Dc != sleep.Ac ? $" ({Time(sleep.Dc.Value)} on battery)" : ""));
        if (profile.Get("lid") is { Dc: { } lid } lidPair) { string Lid(int value) => Catalog.Find("lid")!.Describe(value).ToLowerInvariant().Replace("do nothing", "keep running"); parts.Add($"closing the lid: {Lid(lid)}{(profile.Get("hibernateAfter")?.Dc is > 0 ? ", then hibernate" : "")} on battery" + (lidPair.Ac is { } lidAc ? $", {Lid(lidAc)} plugged in" : "")); }
        if (profile.SpeedAc is { } speed) parts.Add(Catalog.SpeedLabels[speed].ToLowerInvariant() + (profile.SpeedDc is { } speedDc && speedDc != speed ? $" ({Catalog.SpeedLabels[speedDc].ToLowerInvariant()} on battery)" : ""));
        if (profile is Tweak { VolumeChange: { } change }) parts.Add($"volume {(change > 0 ? "up" : "down")} {Math.Abs(change)}%");
        var sentence = string.Join(", ", parts) + ".";
        return char.ToUpperInvariant(sentence[0]) + sentence[1..];
    }

    static Button Button(string label, Action action, bool primary = false)
    {
        var button = new Button { Content = label, Padding = new Thickness(18, 6, 18, 6), FontSize = 13.5, MinWidth = 90 };
        if (primary) { button.FontWeight = FontWeights.SemiBold; button.IsDefault = true; }
        button.Click += (sender, args) => action();
        return button;
    }
}
