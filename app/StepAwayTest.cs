using System.Diagnostics.Eventing.Reader;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using WinSwitch.Apps;
using WinSwitch.Power;
using WinSwitch.Shell;
namespace WinSwitch;

// "When I step away" › Test (Mat 18-19 Nov, mockups 12 and 13): the line in effect now with a live idle timer, the last time away,
// what's keeping the PC awake (with a way to have Windows ignore it, noted for Original settings), and buttons to try each step
public sealed class StepAwayTest : StackPanel
{
    sealed record Step(string Key, string Name, int Seconds, Color Fill, string Doing);
    sealed class Request { public string Type = "", Name = "", Label = "", Detail = ""; public HashSet<string> Kinds = new(); }
    readonly DispatcherTimer clockTimer = new() { Interval = TimeSpan.FromMilliseconds(250) }, slowTimer = new() { Interval = TimeSpan.FromSeconds(5) };
    readonly Func<string, int, bool> setOnProfile; // wake timers on the profile in use; false when that's the Starting profile
    readonly Func<string, bool> available;
    List<Step> line = new(); int lockSeconds, dimmed; string lineKey = "", drawnKey = "", confirming = "", problem = "";
    readonly TextBlock caption = Words("", 12, quietInk), clock = new() { FontSize = 30, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White, FontFamily = new FontFamily("Segoe UI Variable Display, Segoe UI"), MinWidth = 96 }, stateText = Words("", 12.5, Brushes.White, true), what = Words("", 12.5, softInk), next = Words("", 12, softInk), lockedBadge = Words("🔒 Locked", 12, lockInk, true), heldText = Words("", 12.5, Brushes.White), lastAway = Words("", 12, softInk);
    readonly Ellipse led = new() { Width = 9, Height = 9, Margin = new Thickness(0, 0, 7, 0) };
    readonly Border statePill = new() { CornerRadius = new CornerRadius(11), Padding = new Thickness(9, 2, 10, 3), Margin = new Thickness(12, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center }, held = new() { CornerRadius = new CornerRadius(7), Padding = new Thickness(10, 6, 10, 7), Margin = new Thickness(0, 10, 0, 0), Background = new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0x5A, 0x5A)), Visibility = Visibility.Collapsed };
    readonly Canvas mini = new() { Height = 16, Margin = new Thickness(0, 12, 0, 0), ClipToBounds = false };
    readonly Rectangle head = new() { Width = 2, Height = 24, Fill = Brushes.White, Effect = new System.Windows.Media.Effects.DropShadowEffect { Color = Colors.White, BlurRadius = 8, ShadowDepth = 0 } }, heldMark = new() { Width = 3, Height = 24, Fill = new SolidColorBrush(Color.FromRgb(0xFF, 0x5A, 0x5A)) };
    readonly StackPanel keeping = new();
    static readonly Brush quietInk = new SolidColorBrush(Color.FromRgb(0x66, 0x6B, 0x75)), softInk = new SolidColorBrush(Color.FromRgb(0xB8, 0xC0, 0xCC)), lockInk = new SolidColorBrush(Color.FromRgb(0xF0, 0x8A, 0x4B)), orangeInk = new SolidColorBrush(Color.FromRgb(0xC2, 0x41, 0x0C)), goodInk = new SolidColorBrush(Color.FromRgb(0x1D, 0x6B, 0x35));
    static readonly Dictionary<string, (Color fill, Color ink)> colours = new() { ["dimAfter"] = (Color.FromRgb(0xF4, 0xC4, 0x6A), Color.FromRgb(0x4A, 0x33, 0x00)), ["blankAfter"] = (Color.FromRgb(0x3A, 0x3F, 0x48), Color.FromRgb(0xE9, 0xEC, 0xF0)), ["screenOff"] = (Color.FromRgb(0x05, 0x06, 0x08), Color.FromRgb(0xC9, 0xCE, 0xD6)), ["lock"] = (Color.FromRgb(0xFF, 0xF4, 0xEE), Color.FromRgb(0xC2, 0x41, 0x0C)), ["sleepAfter"] = (Color.FromRgb(0x3B, 0x3F, 0x8F), Color.FromRgb(0xE6, 0xE8, 0xFF)), ["hibernateAfter"] = (Color.FromRgb(0x1E, 0x3A, 0x5F), Color.FromRgb(0xDB, 0xE9, 0xFF)) };
    static readonly Color idleFill = Color.FromRgb(0x58, 0xE2, 0x8C);

    public StepAwayTest(Func<string, bool> available, Func<string, int, bool> setOnProfile)
    {
        this.available = available; this.setOnProfile = setOnProfile; Margin = new Thickness(0, 8, 0, 0);
        // The timer
        statePill.Child = new StackPanel { Orientation = Orientation.Horizontal, Children = { led, stateText } };
        var top = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(next, Dock.Right); next.TextAlignment = TextAlignment.Right; next.VerticalAlignment = VerticalAlignment.Center;
        top.Children.Add(next); top.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Children = { clock, statePill, lockedBadge } });
        lockedBadge.VerticalAlignment = VerticalAlignment.Center;
        held.Child = heldText; mini.Children.Add(heldMark); mini.Children.Add(head);
        mini.SizeChanged += (sender, args) => { drawnKey = ""; Update(); };
        Children.Add(caption);
        Children.Add(new Border { Background = new SolidColorBrush(Color.FromRgb(0x17, 0x1A, 0x21)), CornerRadius = new CornerRadius(10), Padding = new Thickness(16, 12, 16, 14), Margin = new Thickness(0, 6, 0, 0), Child = new StackPanel { Children = { top, what, mini, held, new Border { BorderBrush = new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)), BorderThickness = new Thickness(0, 1, 0, 0), Margin = new Thickness(0, 12, 0, 0), Padding = new Thickness(0, 8, 0, 0), Child = lastAway } } } });
        what.Margin = new Thickness(0, 6, 0, 0);
        // What's keeping it awake
        Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 16, 0, 6), Children = { Words("What's keeping it awake", 14, null, true), Words("  ● live", 11.5, goodInk) } });
        Children.Add(keeping);
        // Try a step
        Children.Add(Words("Try a step now", 14, null, true, new Thickness(0, 16, 0, 6)));
        var tries = new WrapPanel();
        void Try(string key, string name, string note, bool show) { if (show) tries.Children.Add(Tile(key, name, note)); }
        Try("dimAfter", "Dim", "To the dimmed brightness for 5 s", available("dimAfter") && Direct.Brightness() != null);
        Try("blankAfter", "Blank", "Any key brings it back", true);
        Try("screenOff", "Screen off", "Any key brings it back", available("screenOff"));
        Try("lock", "🔒 Lock", "Sign in to come back", true);
        Try("sleepAfter", "Sleep 🌙", "Power button wakes it", available("sleepAfter"));
        Try("hibernateAfter", "Hibernate ❄", "Power button, about 20 s", available("hibernateAfter") && PowerApi.CanHibernate());
        Children.Add(tries);
        var fast = new Button { Content = "▶  Play the line fast", Padding = new Thickness(12, 4, 12, 5), Margin = new Thickness(0, 6, 10, 0) };
        fast.Click += async (sender, args) => await PlayFastAsync();
        Children.Add(new WrapPanel { Children = { fast, Words("Dim, Blank and Screen off, 3 s each, stopping before Lock. Any key stops it.", 12, quietInk, false, new Thickness(0, 10, 0, 0)) } });
        clockTimer.Tick += (sender, args) => Update();
        slowTimer.Tick += async (sender, args) => { ReadLine(); await FillKeepingAsync(); };
        Loaded += async (sender, args) => { ReadLine(); Update(); clockTimer.Start(); slowTimer.Start(); await FillKeepingAsync(); };
        Unloaded += (sender, args) => { clockTimer.Stop(); slowTimer.Stop(); };
    }

    // ---- The line Windows and WinSwitch will follow right now: Windows' timers from the plan in use, Blank and Lock from the profile in effect ----
    void ReadLine()
    {
        var pluggedIn = PowerApi.PowerStatus().pluggedIn; var plan = PowerApi.ActiveScheme();
        int Plan(string key) => plan is { } scheme && Catalog.Find(key) is { } setting && Reader.ReadPlan(scheme, setting, pluggedIn) is { } value ? (int)value : 0;
        int Ours(string key) => App.Instance.Engine.EffectiveValues.TryGetValue(key, out var pair) ? (pluggedIn ? pair.Ac : pair.Dc) ?? 0 : 0;
        dimmed = Plan("dimmedBrightness"); lockSeconds = Ours("lockAfter");
        var steps = new List<Step>();
        void Add(string key, string name, int seconds, string doing) { if (seconds > 0 && seconds < 2_678_400) steps.Add(new Step(key, name, seconds, colours[key].fill, doing)); }
        if (available("dimAfter")) Add("dimAfter", "Dim", Plan("dimAfter"), $"Screen dimmed to {dimmed}%.");
        Add("blankAfter", "Blank", Ours("blankAfter"), "Black cover on every screen. Any key brings it straight back.");
        Add("screenOff", "Screen off", Plan("screenOff"), "Screen switched off. Back in a second.");
        Add("sleepAfter", "Sleep", Plan("sleepAfter"), "Asleep. The power button wakes it.");
        Add("hibernateAfter", "Hibernate", Plan("hibernateAfter"), "Saved to disk and off.");
        line = steps.OrderBy(step => step.Seconds).ToList();
        lineKey = string.Join(",", line.Select(step => step.Key + step.Seconds)) + "|" + lockSeconds;
        caption.Text = $"The line in effect now: {App.Instance.Engine.ActiveProfile?.Name ?? "Windows' own settings (WinSwitch is paused)"}, {(pluggedIn ? "plugged in" : "on battery")}. Stop touching the keyboard and mouse to watch it count.";
    }

    // ---- The timer, four times a second ----
    void Update()
    {
        var idle = IdleWatcher.IdleSeconds; var using_ = idle < 2;
        int SecondsOf(string key) => line.FirstOrDefault(step => step.Key == key)?.Seconds ?? 0;
        var heldStep = using_ ? null : SecondsOf("screenOff") is > 0 and var off && idle >= off + 10 && IdleWatcher.DisplayState != 0 ? line.First(step => step.Key == "screenOff") : SecondsOf("sleepAfter") is > 0 and var sleep && idle >= sleep + 10 ? line.First(step => step.Key == "sleepAfter") : null;
        var current = using_ ? -1 : line.FindLastIndex(step => idle >= step.Seconds && (heldStep == null || step.Seconds < heldStep.Seconds));
        var step = current >= 0 ? line[current] : null; var following = line.Skip(current + 1).FirstOrDefault();
        clock.Text = using_ ? "0:00" : Clock(idle);
        var colour = using_ ? idleFill : heldStep != null ? Color.FromRgb(0xFF, 0x5A, 0x5A) : step?.Fill ?? idleFill;
        if (step?.Key == "screenOff") colour = Color.FromRgb(0xE9, 0xEC, 0xF0);
        led.Fill = new SolidColorBrush(colour); statePill.Background = new SolidColorBrush(Color.FromArgb(0x30, colour.R, colour.G, colour.B));
        stateText.Text = using_ ? "Not idle" : heldStep != null ? $"Idle · {heldStep.Name} held" : $"Idle · {step?.Name ?? "nothing yet"}";
        what.Text = using_ ? "You're using the PC. Stop touching the keyboard and mouse to watch it count." : heldStep != null ? $"{heldStep.Name} should have happened at {Clock(heldStep.Seconds)}, but something is holding it." : step?.Doing ?? "Screen as normal. Nothing has happened yet.";
        next.Text = heldStep != null ? "" : string.Join("\n", new[] { lockSeconds > 0 && idle < lockSeconds ? $"Lock in {Clock(lockSeconds - idle)}" : "", following != null ? $"{following.Name} in {Clock(following.Seconds - (using_ ? 0 : idle))}" : "" }.Where(text => text != ""));
        lockedBadge.Visibility = !using_ && lockSeconds > 0 && idle >= lockSeconds ? Visibility.Visible : Visibility.Collapsed;
        held.Visibility = heldStep != null ? Visibility.Visible : Visibility.Collapsed;
        if (heldStep != null) heldText.Text = $"⛔ {heldStep.Name} is being held. {HeldReason(heldStep.Key == "screenOff")}";
        DrawLine(current, using_);
        var width = mini.ActualWidth;
        Canvas.SetLeft(head, X(using_ ? 0 : idle, width) - 1); Canvas.SetTop(head, -4); head.Visibility = using_ ? Visibility.Hidden : Visibility.Visible;
        heldMark.Visibility = heldStep != null ? Visibility.Visible : Visibility.Collapsed; if (heldStep != null) { Canvas.SetLeft(heldMark, X(heldStep.Seconds, width) - 1.5); Canvas.SetTop(heldMark, -4); }
        lastAway.Text = AwayText(IdleWatcher.Current is { Events.Count: > 0 } now && !using_ ? now : IdleWatcher.Last, IdleWatcher.Current != null && !using_);
    }

    string HeldReason(bool screen)
    {
        if (PowerActions.Awake) return "Keep awake is on in WinSwitch.";
        var flags = CallNtPowerInformation(16 /* SystemExecutionState */, IntPtr.Zero, 0, out var state, sizeof(uint)) == 0 ? state : 0;
        if ((flags & (screen ? 0x2u : 0x1u)) != 0) return "Something is asking Windows to stay awake: see What's keeping it awake below.";
        return "Nothing is asking Windows to stay awake right now. Check below for a mouse that moves by itself.";
    }

    string AwayText(IdleWatcher.Away? away, bool now)
    {
        if (away == null) return "Last time away: nothing yet. Each time you step away, what happened and when shows here.";
        var events = away.Events.Select(happened => $"{happened.What} {Clock(happened.AtSeconds)}");
        var early = away.Events.FirstOrDefault(happened => happened.What == "Locked by Windows" && (lockSeconds == 0 || happened.AtSeconds < lockSeconds - 5));
        return (now ? "Away now: " : $"Last time away: {away.Started:h:mm tt}, {Clock(away.Seconds)}.  ") + (away.Events.Count == 0 ? "nothing happened." : string.Join("  ·  ", events)) + (early != null ? $"\n⚠ Windows locked it at {Clock(early.AtSeconds)}, before your Lock{(lockSeconds > 0 ? " at " + Clock(lockSeconds) : "")}." : "");
    }

    // The small line: stages in their colours, the one you're in marching, done ones faded, Lock dashed; redrawn only when something changes
    void DrawLine(int current, bool using_)
    {
        var key = lineKey + "|" + current + "|" + using_ + "|" + mini.ActualWidth; if (key == drawnKey) return; drawnKey = key;
        foreach (var old in mini.Children.OfType<FrameworkElement>().Where(element => element != head && element != heldMark).ToList()) mini.Children.Remove(old);
        var width = mini.ActualWidth; if (width < 50) return;
        var edges = new List<(double from, double to, Color fill, int index)> { (0, line.Count > 0 ? X(line[0].Seconds, width) : width, idleFill, -1) };
        for (var index = 0; index < line.Count; index++) edges.Add((X(line[index].Seconds, width), index + 1 < line.Count ? X(line[index + 1].Seconds, width) : width, line[index].Key == "blankAfter" ? Color.FromRgb(0x3A, 0x3F, 0x48) : line[index].Fill, index));
        foreach (var (from, to, fill, index) in edges)
        {
            var now = !using_ && index == current; var done = !using_ && index < current;
            var segment = new Border { Width = Math.Max(1, to - from - 1), Height = 16, CornerRadius = new CornerRadius(index == -1 ? 4 : 0, index == line.Count - 1 || line.Count == 0 ? 4 : 0, index == line.Count - 1 || line.Count == 0 ? 4 : 0, index == -1 ? 4 : 0), Background = now ? Stripes(fill) : new SolidColorBrush(fill), Opacity = done ? 0.3 : now ? 1 : 0.75, BorderBrush = new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)), BorderThickness = new Thickness(fill.R + fill.G + fill.B < 40 ? 1 : 0) };
            Canvas.SetLeft(segment, from); mini.Children.Insert(0, segment);
        }
        if (lockSeconds > 0) { var mark = new Line { X1 = 0, Y1 = -4, X2 = 0, Y2 = 20, Stroke = lockInk, StrokeThickness = 2, StrokeDashArray = new DoubleCollection { 2, 2 } }; Canvas.SetLeft(mark, X(lockSeconds, width)); mini.Children.Insert(mini.Children.Count - 2, mark); var pin = Words("🔒", 10, null); Canvas.SetLeft(pin, X(lockSeconds, width) - 6); Canvas.SetTop(pin, -17); mini.Children.Insert(mini.Children.Count - 2, pin); }
    }

    static Brush Stripes(Color fill)
    {
        var light = Color.FromArgb(0xFF, (byte)Math.Min(255, fill.R + 40), (byte)Math.Min(255, fill.G + 40), (byte)Math.Min(255, fill.B + 40));
        var brush = new LinearGradientBrush { MappingMode = BrushMappingMode.Absolute, SpreadMethod = GradientSpreadMethod.Repeat, StartPoint = new Point(0, 0), EndPoint = new Point(6, 6), GradientStops = { new GradientStop(fill, 0), new GradientStop(fill, 0.5), new GradientStop(light, 0.5), new GradientStop(light, 1) } };
        var move = new TranslateTransform(); brush.Transform = move;
        move.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(0, 12, TimeSpan.FromSeconds(0.6)) { RepeatBehavior = RepeatBehavior.Forever });
        return brush;
    }

    // Same scale as the line above: one slot per stop (1, 2, 3, 5, 10, 15, 20, 30, 45 min, 1, 2, 3, 5 h), Never at the end
    static double X(int seconds, double width)
    {
        var stops = IdleTimeline.Stops; double slot;
        if (seconds <= 0) slot = 0; else if (seconds <= stops[0]) slot = seconds / (double)stops[0]; else if (seconds >= stops[^1]) slot = Math.Min(14, stops.Length + (seconds - stops[^1]) / (double)stops[^1]);
        else { var index = Array.FindIndex(stops, stop => seconds <= stop); slot = index + (seconds - stops[index - 1]) / (double)(stops[index] - stops[index - 1]); }
        return slot / 14 * width;
    }

    static string Clock(int seconds) => seconds >= 3600 ? $"{seconds / 3600}:{seconds % 3600 / 60:00}:{seconds % 60:00}" : $"{seconds / 60}:{seconds % 60:00}";

    // ---- What's keeping it awake: power requests (through the helper), mice that move by themselves, and what woke it last ----
    async Task FillKeepingAsync()
    {
        var rows = new List<UIElement>(); var ignored = App.Instance.Settings.Power.Ignored;
        var helperReady = HelperClient.Installed && !HelperClient.NeedsUpdate(HelperClient.InstalledVersion);
        if (helperReady && await HelperClient.SendAsync("requests", null, 8000) is { } reply)
            foreach (var request in Parse(reply["value"]?.GetValue<string>() ?? "").Where(request => !ignored.Any(entry => entry.Type == request.Type && entry.Name.Equals(request.Name, StringComparison.OrdinalIgnoreCase)))) rows.Add(RequestRow(request));
        else
        {
            var flags = CallNtPowerInformation(16, IntPtr.Zero, 0, out var state, sizeof(uint)) == 0 ? state : 0;
            var say = (flags & 0x2) != 0 ? "Something is keeping the screen on right now." : (flags & 0x1) != 0 ? "Something is stopping Sleep right now." : "Nothing is asking Windows to stay awake right now.";
            rows.Add(Row("🔎", "Apps, services and drivers", "", Array.Empty<string>(), $"{say} The helper can name them and tell Windows to ignore one.", new UIElement[] { Act(HelperClient.Installed ? "Update the helper" : "Install the helper", async () => { await HelperClient.InstallAsync(); await FillKeepingAsync(); }, true) }));
        }
        foreach (var entry in ignored.ToList())
            rows.Add(Row("✓", entry.Label, "ignored", Array.Empty<string>(), $"Windows ignores it when deciding to {Deciding(entry.Requests)}. Noted in Original settings.", new UIElement[] { Act("Undo", async () => { if (await HelperClient.DoAsync("requests-override", new { type = entry.Type, name = entry.Name, requests = "" })) { ignored.Remove(entry); App.Instance.Settings.Save(); } else problem = "The helper couldn't undo it. Its log says why."; await FillKeepingAsync(); }) }));
        foreach (var device in StrayInput.Devices.Values.Where(device => (DateTime.Now - device.LastMove).TotalHours < 24))
            rows.Add(Row("🖱", device.Name, "mouse", new[] { "resets the idle clock" }, $"Moved {device.Moves} time{(device.Moves == 1 ? "" : "s")} by itself while you were away, the last {Ago(device.LastMove)} ago. Usually a sensor on a shiny desk or a mouse knocked in a bag: turn it over or switch it off when you leave.", Array.Empty<UIElement>()));
        if (await Task.Run(LastWake) is { } wake) rows.Add(await WakeRowAsync(wake.at, wake.source));
        if (rows.Count == 0) rows.Add(Words("Nothing is keeping it awake right now.", 12.5, quietInk));
        if (problem != "") rows.Insert(0, Words(problem, 12.5, orangeInk, true, new Thickness(0, 0, 0, 6)));
        keeping.Children.Clear(); foreach (var row in rows) keeping.Children.Add(row);
    }

    UIElement RequestRow(Request request)
    {
        if (request.Type == "PROCESS" && request.Name.Equals("WinSwitch.exe", StringComparison.OrdinalIgnoreCase))
            return Row("☕", "WinSwitch", "this app", Tags(request), PowerActions.Awake ? "Keep awake is on." : request.Detail, PowerActions.Awake ? new UIElement[] { Act("Stop keeping awake", async () => { PowerActions.StopKeepingAwake(); await FillKeepingAsync(); }) } : Array.Empty<UIElement>());
        var key = request.Type + "|" + request.Name; var deciding = Deciding(string.Join(" ", request.Kinds));
        var icon = request.Type == "PROCESS" ? "🎞" : request.Detail.Contains("audio", StringComparison.OrdinalIgnoreCase) ? "🔊" : request.Type == "SERVICE" ? "⚙" : "🔌";
        var kind = request.Type == "PROCESS" ? "app" : request.Type == "SERVICE" ? "service" : "driver";
        if (confirming == key)
            return Row(icon, request.Label, kind, Tags(request), $"Do you want me to tell Windows to ignore {request.Label} when deciding to {deciding}? I'll note it first, so you can put it back any time in Settings › Original settings.", new UIElement[] {
                Act("Do it", async () => { confirming = ""; var requests = string.Join(" ", request.Kinds.Intersect(new[] { "DISPLAY", "SYSTEM", "AWAYMODE" }));
                    if (await HelperClient.DoAsync("requests-override", new { type = request.Type, name = request.Name, requests })) { App.Instance.Settings.Power.Ignored.Add(new Settings.IgnoredRequest { Type = request.Type, Name = request.Name, Label = request.Label, Requests = requests }); App.Instance.Settings.Save(); problem = ""; }
                    else problem = $"Windows wouldn't ignore {request.Label}. The helper's log says why.";
                    await FillKeepingAsync(); }, true),
                Act("Cancel", async () => { confirming = ""; await FillKeepingAsync(); }) }, highlight: true);
        return Row(icon, request.Label, kind, Tags(request), request.Detail, new UIElement[] { Act("Ignore it", async () => { confirming = key; await FillKeepingAsync(); }) });
    }

    async Task<UIElement> WakeRowAsync(DateTime at, string source)
    {
        var when = at.Date == DateTime.Today ? $"Woke at {at:HH:mm}" : $"Woke at {at:HH:mm} yesterday";
        if (source.StartsWith("Timer", StringComparison.OrdinalIgnoreCase))
        {
            var task = Regex.Match(source, @"'([^']+)'").Groups[1].Value; var name = task == "" ? "A scheduled task" : task.Split('\\')[^1];
            var plan = PowerApi.ActiveScheme(); var pluggedIn = PowerApi.PowerStatus().pluggedIn;
            var timersOn = plan is { } scheme && Catalog.Find("wakeTimers") is { } setting && Reader.ReadPlan(scheme, setting, pluggedIn) == 1;
            var actions = timersOn ? new UIElement[] { Act("Important ones only", async () => { if (!setOnProfile("wakeTimers", 2)) problem = "The Starting profile is in use: set Wake timers in What this sets after Edit…"; await FillKeepingAsync(); }) } : Array.Empty<UIElement>();
            return Row("⏰", when, "wake timer", new[] { "wakes it" }, $"{name} woke the PC.{(timersOn ? " Wake timers can be limited to important ones only." : "")}", actions);
        }
        var device = Regex.Replace(source, @"^Device\s*-\s*", "").Trim();
        var actionsForDevice = new List<UIElement>();
        if (HelperClient.Installed && await HelperClient.StateAsync() is { } state && state["devices"]!.AsArray().FirstOrDefault(entry => entry?["name"]?.GetValue<string>() == device && entry?["allowWake"]?.GetValue<bool>() == true) is { } found)
        {
            var id = found["id"]!.GetValue<string>(); var key = "wake|" + id;
            if (confirming == key) return Row("⚡", when, "device", new[] { "wakes it" }, $"Do you want me to stop {device} waking the PC? Windows keeps its own record of the old setting, and Settings › Helper › Devices that can wake this PC puts it back.", new UIElement[] { Act("Do it", async () => { confirming = ""; if (!await HelperClient.DoAsync("device", new { id, allowWake = false })) problem = $"Windows wouldn't change {device}."; await FillKeepingAsync(); }, true), Act("Cancel", async () => { confirming = ""; await FillKeepingAsync(); }) }, highlight: true);
            actionsForDevice.Add(Act("Don't let it wake the PC", async () => { confirming = key; await FillKeepingAsync(); }));
        }
        else if (!HelperClient.Installed) actionsForDevice.Add(Words("needs the helper", 11.5, quietInk));
        return Row("⚡", when, "device", new[] { "wakes it" }, $"{device} woke the PC.", actionsForDevice.ToArray());
    }

    // The latest wake that wasn't you (power button, lid) in the last day and a half, from Windows' System log
    static (DateTime at, string source)? LastWake()
    {
        try
        {
            using var reader = new EventLogReader(new EventLogQuery("System", PathType.LogName, "*[System[Provider[@Name='Microsoft-Windows-Power-Troubleshooter'] and (EventID=1)]]") { ReverseDirection = true });
            using var record = reader.ReadEvent();
            if (record?.TimeCreated is not { } at || (DateTime.Now - at).TotalHours > 36) return null;
            var source = Regex.Match(record.FormatDescription() ?? "", @"Wake Source:\s*(.+)").Groups[1].Value.Trim();
            return source == "" || Regex.IsMatch(source, "power button|lid|unknown|input|keyboard|mouse", RegexOptions.IgnoreCase) ? null : (at, source);
        }
        catch { return null; }
    }

    static List<Request> Parse(string text)
    {
        var found = new Dictionary<string, Request>(); var section = ""; Request? last = null;
        foreach (var raw in text.Split('\n'))
        {
            var lineText = raw.Trim();
            if (Regex.Match(lineText, @"^([A-Z]+):$") is { Success: true } header) { section = header.Groups[1].Value; last = null; continue; }
            if (Regex.Match(lineText, @"^\[(PROCESS|SERVICE|DRIVER)\]\s+(.+)$") is { Success: true } entry)
            {
                var type = entry.Groups[1].Value; var (name, label) = Names(type, entry.Groups[2].Value.Trim());
                if (!found.TryGetValue(type + "|" + name, out last)) found[type + "|" + name] = last = new Request { Type = type, Name = name, Label = label };
                last.Kinds.Add(section); continue;
            }
            if (last != null && lineText != "" && last.Detail == "") last.Detail = lineText;
        }
        return found.Values.Where(request => request.Kinds.Overlaps(new[] { "DISPLAY", "SYSTEM", "AWAYMODE" })).ToList();
    }

    // The name powercfg /requestsoverride wants, and a friendly one: chrome.exe / Google Chrome, WSearch / Windows Search, the driver's name
    static (string name, string label) Names(string type, string text)
    {
        if (type == "PROCESS") { var path = DosPath(text); var file = System.IO.Path.GetFileName(path); string label; try { label = File.Exists(path) ? AppCatalog.FriendlyName(path) : file; } catch { label = file; } return (file, label); }
        if (type == "SERVICE") { var name = Regex.Match(text, @"\(([^()]+)\)\s*$").Groups[1].Value; if (name == "") name = text; try { using var service = new System.ServiceProcess.ServiceController(name); return (name, service.DisplayName); } catch { return (name, name); } }
        var driver = Regex.Replace(text, @"\s*\([^()]*\\[^()]*\)\s*$", ""); return (driver, driver);
    }

    // \Device\HarddiskVolume3\Program Files\… → C:\Program Files\…
    static string DosPath(string devicePath)
    {
        foreach (var drive in Environment.GetLogicalDrives())
        {
            var letter = drive.TrimEnd('\\'); var target = new StringBuilder(260);
            if (QueryDosDevice(letter, target, 260) > 0 && devicePath.StartsWith(target + "\\", StringComparison.OrdinalIgnoreCase)) return letter + devicePath[target.Length..];
        }
        return devicePath;
    }

    static string[] Tags(Request request) => new[] { request.Kinds.Contains("DISPLAY") ? "keeps the screen on" : "", request.Kinds.Contains("SYSTEM") || request.Kinds.Contains("AWAYMODE") ? "stops Sleep" : "" }.Where(tag => tag != "").ToArray();
    static string Deciding(string requests) => requests.Contains("DISPLAY") && (requests.Contains("SYSTEM") || requests.Contains("AWAYMODE")) ? "turn the screen off or sleep" : requests.Contains("DISPLAY") ? "turn the screen off" : "sleep";
    static string Ago(DateTime then) { var span = DateTime.Now - then; return span.TotalMinutes < 1 ? $"{(int)span.TotalSeconds} s" : span.TotalHours < 1 ? $"{(int)span.TotalMinutes} min" : $"{(int)span.TotalHours} h"; }

    // ---- Try a step ----
    Border Tile(string key, string name, string note)
    {
        var (fill, ink) = colours[key];
        var tile = new Border { Background = new SolidColorBrush(fill), BorderBrush = key == "lock" ? orangeInk : null, BorderThickness = new Thickness(key == "lock" ? 1.5 : 0), CornerRadius = new CornerRadius(8), Padding = new Thickness(12, 8, 14, 9), Margin = new Thickness(0, 0, 8, 8), MinWidth = 130, Cursor = System.Windows.Input.Cursors.Hand, ToolTip = "Try it now",
            Child = new StackPanel { Children = { Words("▶  " + name, 13, new SolidColorBrush(ink), true), Words(note, 11.5, new SolidColorBrush(Color.FromArgb(0xCC, ink.R, ink.G, ink.B))) } } };
        tile.MouseLeftButtonUp += async (sender, args) => await TryAsync(key);
        return tile;
    }

    async Task TryAsync(string key)
    {
        switch (key)
        {
            case "dimAfter": if (Direct.Brightness() is not { } before) return; Direct.SetBrightness(dimmed > 0 ? dimmed : 30); await Task.Delay(5000); Direct.SetBrightness(before); break;
            case "blankAfter": await Task.Delay(800); IdleWatcher.BlankNow(); break; // after the click, so letting go of the mouse doesn't end it
            case "screenOff": await Task.Delay(800); PowerActions.WindowsScreenOff(); break; // as Windows' timer does it, standby included
            case "lock": PowerActions.Lock(); break;
            case "sleepAfter": PowerActions.Sleep(); break;
            case "hibernateAfter": PowerActions.Hibernate(); break;
        }
    }

    // Dim, Blank and Screen off one after another, 3 s each; any key or mouse move stops it and puts the brightness back
    async Task PlayFastAsync()
    {
        await Task.Delay(800); var start = IdleWatcher.LastInput(); var before = Direct.Brightness();
        bool Touched() => IdleWatcher.LastInput() != start;
        async Task<bool> Wait(int milliseconds) { for (var waited = 0; waited < milliseconds; waited += 100) { await Task.Delay(100); if (Touched()) return false; } return true; }
        try
        {
            if (before != null && available("dimAfter")) { Direct.SetBrightness(dimmed > 0 ? dimmed : 30); if (!await Wait(3000)) return; }
            IdleWatcher.BlankNow(); if (!await Wait(3000)) return;
            if (available("screenOff")) { PowerActions.WindowsScreenOff(); while (!Touched()) await Task.Delay(250); }
        }
        finally { if (before != null) Direct.SetBrightness(before.Value); }
    }

    // ---- Small parts ----
    static TextBlock Words(string text, double size, Brush? colour, bool bold = false, Thickness? margin = null) => new() { Text = text, FontSize = size, Foreground = colour ?? Brushes.Black, FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal, TextWrapping = TextWrapping.Wrap, Margin = margin ?? new Thickness(0) };
    static Button Act(string label, Func<Task> action, bool primary = false) { var button = new Button { Content = label, Padding = new Thickness(10, 2, 10, 3), Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center }; if (primary) button.Style = (Style)Application.Current.FindResource("Primary"); button.Click += async (sender, args) => await action(); return button; }

    static Border Row(string icon, string title, string kind, string[] tags, string detail, UIElement[] actions, bool highlight = false)
    {
        var heading = new WrapPanel { Children = { Words(title, 13, null, true), Words("  " + kind, 11.5, quietInk, false, new Thickness(0, 1.5, 6, 0)) } };
        foreach (var tag in tags) heading.Children.Add(new Border { Background = new SolidColorBrush(tag.StartsWith("keeps") ? Color.FromRgb(0xE9, 0xEC, 0xF0) : tag.StartsWith("stops") ? Color.FromRgb(0xE4, 0xE5, 0xF6) : tag.StartsWith("resets") ? Color.FromRgb(0xE3, 0xF3, 0xE8) : Color.FromRgb(0xFB, 0xE8, 0xDF)), CornerRadius = new CornerRadius(8), Padding = new Thickness(7, 0, 7, 1), Margin = new Thickness(0, 1, 4, 0), Child = Words(tag, 11, quietInk, true) });
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) }; foreach (var action in actions) buttons.Children.Add(action);
        var dock = new DockPanel(); DockPanel.SetDock(buttons, Dock.Right); dock.Children.Add(buttons);
        dock.Children.Add(new DockPanel { Children = { new Border { Width = 30, Height = 30, CornerRadius = new CornerRadius(7), Background = new SolidColorBrush(Color.FromRgb(0xF1, 0xF3, 0xF6)), Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Top, Child = new TextBlock { Text = icon, FontSize = 15, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } }, new StackPanel { Children = { heading, Words(detail, 12, quietInk, false, new Thickness(0, 2, 0, 0)) } } } });
        return new Border { Child = dock, Padding = new Thickness(10, 8, 10, 8), Margin = new Thickness(0, 0, 0, 6), CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(highlight ? 1.5 : 1), BorderBrush = new SolidColorBrush(highlight ? Color.FromRgb(0x2F, 0x7F, 0xD8) : Color.FromRgb(0xE3, 0xE5, 0xE8)), Background = Brushes.White };
    }

    [DllImport("powrprof.dll")] static extern uint CallNtPowerInformation(int level, IntPtr input, int inputLength, out uint output, int outputLength);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern uint QueryDosDevice(string device, StringBuilder target, int size);
}
