using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
namespace WinSwitch;

// "When I step away" (Mat 18 Nov, mockup 11): one line from your last key or mouse move to Never, in coloured stages (Dim, Blank, Screen off, Sleep, Hibernate).
// Drag an edge to change when a stage starts (it snaps to the stops, a bubble shows the time, neighbours get pushed along), drag a stage off the line to stop using it,
// click it under "Not used" to put it back, and drag the Lock pin anywhere. Arrow keys move the last edge touched one stop
public sealed class IdleTimeline : Canvas
{
    public static readonly int[] Stops = { 60, 120, 180, 300, 600, 900, 1200, 1800, 2700, 3600, 7200, 10800, 18000 }; // slot 1..13; slot 0 = your last input, slot 14 = Never
    public record Stage(string Key, string Name, string Note, Color Fill, Color Ink);
    public event Action? Committed;
    public int DimmedBrightness { get; private set; }
    public string WindowsLocksAt { get; init; } = ""; // the stage where Windows asks for a password anyway, when it won't take "Not needed"
    readonly List<Stage> stages;
    readonly Dictionary<string, int> slots = new(); // 0 = not used
    int lockSlot; // 0 = not used
    const double BarTop = 40, BarHeight = 52, Last = 13;
    string dragging = ""; // stage key (its edge), "segment:" + key, or "lock"
    bool dragOff; string lastEdge = ""; Point pressedAt;
    static readonly Brush quiet = new SolidColorBrush(Color.FromRgb(0x7A, 0x82, 0x8E)), accent = new SolidColorBrush(Color.FromRgb(0x2F, 0x7F, 0xD8)), lockInk = new SolidColorBrush(Color.FromRgb(0xC2, 0x41, 0x0C));

    public IdleTimeline(List<Stage> stages, Dictionary<string, int> seconds, int lockSeconds, int dimmedBrightness)
    {
        this.stages = stages; DimmedBrightness = dimmedBrightness;
        foreach (var stage in stages) slots[stage.Key] = SlotOf(seconds.GetValueOrDefault(stage.Key));
        lockSlot = SlotOf(lockSeconds);
        Normalize(""); Height = 168; Focusable = true; Background = Brushes.Transparent; ClipToBounds = false;
        SizeChanged += (sender, args) => Render();
    }

    public int SecondsOf(string key) => key == "lock" ? Seconds(lockSlot) : Seconds(slots.GetValueOrDefault(key));
    static int Seconds(int slot) => slot is >= 1 and <= 13 ? Stops[slot - 1] : 0;
    static int SlotOf(int seconds) => seconds <= 0 ? 0 : Array.IndexOf(Stops, Stops.OrderBy(stop => Math.Abs(stop - seconds)).First()) + 1;
    public static string Short(int seconds) => seconds == 0 ? "Never" : seconds < 3600 ? $"{seconds / 60} min" : seconds % 3600 == 0 ? $"{seconds / 3600} h" : $"{seconds / 60} min";
    List<string> Used() => stages.Where(stage => slots[stage.Key] > 0).Select(stage => stage.Key).ToList();

    // Keeps the stages in order, one stop apart at least, within the line; the stage just moved wins
    void Normalize(string moved)
    {
        var used = Used(); var at = used.IndexOf(moved);
        for (var index = at - 1; index >= 0; index--) slots[used[index]] = Math.Min(slots[used[index]], slots[used[index + 1]] - 1);
        for (var index = Math.Max(at, 0) + 1; index < used.Count; index++) slots[used[index]] = Math.Max(slots[used[index]], slots[used[index - 1]] + 1);
        if (used.Count > 0 && slots[used[^1]] > Last) { slots[used[^1]] = (int)Last; for (var index = used.Count - 2; index >= 0; index--) slots[used[index]] = Math.Min(slots[used[index]], slots[used[index + 1]] - 1); }
        if (used.Count > 0 && slots[used[0]] < 1) { slots[used[0]] = 1; for (var index = 1; index < used.Count; index++) slots[used[index]] = Math.Max(slots[used[index]], slots[used[index - 1]] + 1); }
    }

    void Add(string key)
    {
        if (key == "lock") { lockSlot = slots.GetValueOrDefault("screenOff") > 0 ? slots["screenOff"] : 6; Commit(); return; }
        var order = stages.FindIndex(stage => stage.Key == key);
        var before = stages.Take(order).Select(stage => slots[stage.Key]).Where(slot => slot > 0).DefaultIfEmpty(0).Max();
        var after = stages.Skip(order + 1).Select(stage => slots[stage.Key]).Where(slot => slot > 0).DefaultIfEmpty(14).Min();
        slots[key] = Math.Max(before + 1, (before + after) / 2); Normalize(key); Commit();
    }

    void Commit() { Render(); Committed?.Invoke(); }

    double Step => Math.Max(10, ActualWidth / 14);
    double X(int slot) => slot * Step;
    int SlotAt(double x) => (int)Math.Clamp(Math.Round(x / Step), 1, Last);

    void Render()
    {
        Children.Clear();
        if (ActualWidth < 50) return;
        var used = Used(); var width = ActualWidth;
        // The line: Idle, then each stage to the next
        var edges = new List<(string key, double left, double right)> { ("idle", 0, used.Count > 0 ? X(slots[used[0]]) : width) };
        for (var index = 0; index < used.Count; index++) edges.Add((used[index], X(slots[used[index]]), index + 1 < used.Count ? X(slots[used[index + 1]]) : width));
        foreach (var (key, left, right) in edges)
        {
            var stage = stages.FirstOrDefault(candidate => candidate.Key == key) ?? new Stage("idle", "Idle", "screen as normal", Color.FromRgb(0xE3, 0xF3, 0xE8), Color.FromRgb(0x1D, 0x6B, 0x35));
            var first = key == "idle"; var last = right >= width - 0.5;
            var leaving = dragOff && dragging == "segment:" + key;
            var content = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(9, 0, 4, 0) };
            content.Children.Add(new TextBlock { Text = leaving ? "Let go to stop using it" : stage.Name, FontWeight = FontWeights.SemiBold, FontSize = 12.5, Foreground = new SolidColorBrush(stage.Ink), TextTrimming = TextTrimming.CharacterEllipsis });
            if (key == "dimAfter" && !leaving) content.Children.Add(DimChip(stage.Ink));
            else if (!leaving) content.Children.Add(new TextBlock { Text = stage.Note, FontSize = 11, Foreground = new SolidColorBrush(Color.FromArgb(0xD0, stage.Ink.R, stage.Ink.G, stage.Ink.B)), TextTrimming = TextTrimming.CharacterEllipsis });
            var segment = new Border { Width = Math.Max(0, right - left - (last ? 0 : 2)), Height = BarHeight, Background = new SolidColorBrush(stage.Fill), Opacity = leaving ? 0.45 : 1, CornerRadius = new CornerRadius(first ? 8 : 0, last ? 8 : 0, last ? 8 : 0, first ? 8 : 0), Child = content, ClipToBounds = true, Cursor = first ? Cursors.Arrow : Cursors.SizeAll, ToolTip = first ? null : $"Drag {stage.Name} off the line to stop using it" };
            Place(segment, left, BarTop);
        }
        // Edges to drag
        foreach (var key in used)
        {
            var handle = new Border { Width = 7, Height = 30, CornerRadius = new CornerRadius(3.5), Background = Brushes.White, BorderBrush = accent, BorderThickness = new Thickness(key == dragging || key == lastEdge && IsKeyboardFocused ? 2.5 : 1.5), Cursor = Cursors.SizeWE };
            Place(handle, X(slots[key]) - 4.5, BarTop + (BarHeight - 30) / 2);
        }
        // Lock pin
        if (lockSlot > 0)
        {
            var x = X(lockSlot);
            var pin = new Border { Background = Brushes.White, BorderBrush = lockInk, BorderThickness = new Thickness(1.5), CornerRadius = new CornerRadius(12), Padding = new Thickness(8, 2, 9, 3), Opacity = dragOff && dragging == "lock" ? 0.45 : 1, Cursor = Cursors.SizeWE, Child = new TextBlock { Text = dragOff && dragging == "lock" ? "Let go to stop locking" : $"🔒 Lock · {Short(Seconds(lockSlot))}", FontWeight = FontWeights.SemiBold, FontSize = 12, Foreground = lockInk } };
            pin.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Place(pin, Math.Clamp(x - pin.DesiredSize.Width / 2, 0, width - pin.DesiredSize.Width), 0);
            Place(new Line { X1 = x, X2 = x, Y1 = pin.DesiredSize.Height, Y2 = BarTop + BarHeight, Stroke = lockInk, StrokeThickness = 2, StrokeDashArray = new DoubleCollection { 2, 2 }, IsHitTestVisible = false }, 0, 0);
        }
        // Time bubble while dragging an edge
        if (dragging != "" && slots.ContainsKey(dragging))
        {
            var bubble = new Border { Background = new SolidColorBrush(Color.FromRgb(0x1F, 0x23, 0x28)), CornerRadius = new CornerRadius(5), Padding = new Thickness(7, 2, 7, 3), Child = new TextBlock { Text = Short(Seconds(slots[dragging])), Foreground = Brushes.White, FontWeight = FontWeights.SemiBold, FontSize = 12 } };
            bubble.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Place(bubble, Math.Clamp(X(slots[dragging]) - bubble.DesiredSize.Width / 2, 0, width - bubble.DesiredSize.Width), BarTop - bubble.DesiredSize.Height - 4);
        }
        // Scale: every stop when there's room, otherwise the main ones
        var main = new HashSet<int> { 0, 1, 2, 4, 5, 6, 8, 10, 11, 13, 14 };
        for (var slot = 0; slot <= 14; slot++)
        {
            if (Step < 40 && !main.Contains(slot)) continue;
            var label = new TextBlock { Text = slot == 0 ? "0" : slot == 14 ? "Never" : Short(Stops[slot - 1]).Replace(" min", "m").Replace(" h", "h"), FontSize = 10.5, Foreground = quiet };
            label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Place(label, slot == 0 ? 0 : slot == 14 ? width - label.DesiredSize.Width : X(slot) - label.DesiredSize.Width / 2, BarTop + BarHeight + 4);
        }
        // Not used: click one to put it back
        var spare = new WrapPanel { Width = width };
        spare.Children.Add(new TextBlock { Text = "Not used:", Foreground = quiet, Margin = new Thickness(0, 4, 8, 0) });
        var unused = stages.Where(stage => slots[stage.Key] == 0).Select(stage => (stage.Key, stage.Name)).ToList();
        if (lockSlot == 0) unused.Add(("lock", "🔒 Lock"));
        foreach (var (key, name) in unused)
        {
            var chip = new Border { BorderBrush = new SolidColorBrush(Color.FromRgb(0xB9, 0xC0, 0xC8)), BorderThickness = new Thickness(1.2), CornerRadius = new CornerRadius(13), Padding = new Thickness(11, 2, 11, 3), Margin = new Thickness(0, 0, 6, 4), Background = new SolidColorBrush(Color.FromRgb(0xFA, 0xFB, 0xFC)), Cursor = Cursors.Hand, ToolTip = "Put it back on the line", Child = new TextBlock { Text = "+ " + name, FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(Color.FromRgb(0x59, 0x61, 0x6D)) } };
            chip.MouseLeftButtonDown += (sender, args) => args.Handled = true;
            chip.MouseLeftButtonUp += (sender, args) => { args.Handled = true; Add(key); };
            spare.Children.Add(chip);
        }
        if (unused.Count == 0) spare.Children.Add(new TextBlock { Text = "nothing. Drag a stage off the line to stop using it.", Foreground = quiet, Margin = new Thickness(0, 4, 0, 0) });
        Place(spare, 0, BarTop + BarHeight + 28);
        // Whether coming back asks for a password
        var windowsSlot = WindowsLocksAt != "" ? slots.GetValueOrDefault(WindowsLocksAt) : 0; var windowsFirst = windowsSlot > 0 && (lockSlot == 0 || windowsSlot < lockSlot);
        var lockText = windowsFirst ? $"⚠ Windows asks for your password from {Short(Seconds(windowsSlot))} ({stages.First(stage => stage.Key == WindowsLocksAt).Name}) on this PC, before Lock." : lockSlot > 0 ? $"Coming back within {Short(Seconds(lockSlot))} never asks for your password." : "Never locks by itself.";
        Place(new TextBlock { Text = lockText, Foreground = windowsFirst ? lockInk : quiet, FontWeight = windowsFirst ? FontWeights.SemiBold : FontWeights.Normal, FontSize = 12, TextWrapping = TextWrapping.Wrap, Width = Math.Max(100, ActualWidth) }, 0, BarTop + BarHeight + 60);
    }

    FrameworkElement DimChip(Color ink)
    {
        var chip = new Border { Background = new SolidColorBrush(Color.FromArgb(0x88, 0xFF, 0xFF, 0xFF)), BorderBrush = new SolidColorBrush(Color.FromArgb(0x30, 0, 0, 0)), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(9), Padding = new Thickness(6, 0, 6, 1), Margin = new Thickness(0, 2, 0, 0), HorizontalAlignment = HorizontalAlignment.Left, Cursor = Cursors.Hand, ToolTip = "How bright the screen is while dimmed", Child = new TextBlock { Text = $"☀ {DimmedBrightness}% ▾", FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(ink) } };
        chip.MouseLeftButtonDown += (sender, args) => args.Handled = true;
        chip.MouseLeftButtonUp += (sender, args) =>
        {
            args.Handled = true;
            var menu = new ContextMenu { PlacementTarget = chip };
            foreach (var percent in new[] { 0, 5, 10, 20, 30, 40, 50 }) { var item = new MenuItem { Header = $"{percent}%", IsChecked = percent == DimmedBrightness }; item.Click += (s, e) => { DimmedBrightness = percent; Commit(); }; menu.Items.Add(item); }
            menu.IsOpen = true;
        };
        return chip;
    }

    void Place(UIElement element, double left, double top) { SetLeft(element, left); SetTop(element, top); Children.Add(element); }

    // ---- Dragging ----
    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs args)
    {
        base.OnMouseLeftButtonDown(args); Focus();
        var point = args.GetPosition(this); pressedAt = point;
        var onBar = point.Y >= BarTop - 4 && point.Y <= BarTop + BarHeight + 4;
        if (lockSlot > 0 && point.Y < BarTop - 6 && Math.Abs(point.X - X(lockSlot)) < 60) dragging = "lock";
        else if (onBar && Used().OrderBy(key => Math.Abs(X(slots[key]) - point.X)).FirstOrDefault() is { } nearest && Math.Abs(X(slots[nearest]) - point.X) <= 8) dragging = lastEdge = nearest;
        else if (onBar && Used().LastOrDefault(key => X(slots[key]) <= point.X) is { } inside) dragging = "segment:" + inside;
        else return;
        CaptureMouse(); dragOff = false; Render(); args.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs args)
    {
        base.OnMouseMove(args);
        if (dragging == "" || !IsMouseCaptured) return;
        var point = args.GetPosition(this);
        if (dragging == "lock") { lockSlot = SlotAt(point.X); dragOff = point.Y > BarTop + BarHeight + 20 || point.Y < -40; }
        else if (dragging.StartsWith("segment:")) dragOff = Math.Abs(point.Y - (BarTop + BarHeight / 2)) > BarHeight / 2 + 30;
        else { var slot = SlotAt(point.X); if (slot != slots[dragging]) { slots[dragging] = slot; Normalize(dragging); } }
        Render();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs args)
    {
        base.OnMouseLeftButtonUp(args);
        if (dragging == "") return;
        if (dragOff && dragging == "lock") lockSlot = 0;
        else if (dragOff && dragging.StartsWith("segment:")) slots[dragging["segment:".Length..]] = 0;
        var changed = !dragging.StartsWith("segment:") || dragOff;
        dragging = ""; dragOff = false; ReleaseMouseCapture();
        if (changed) Commit(); else Render();
    }

    protected override void OnKeyDown(KeyEventArgs args)
    {
        base.OnKeyDown(args);
        if (lastEdge == "" || slots.GetValueOrDefault(lastEdge) == 0 || args.Key is not (Key.Left or Key.Right)) return;
        slots[lastEdge] = (int)Math.Clamp(slots[lastEdge] + (args.Key == Key.Right ? 1 : -1), 1, Last); Normalize(lastEdge); Commit(); args.Handled = true;
    }
    protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs args) { base.OnGotKeyboardFocus(args); Render(); }
    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs args) { base.OnLostKeyboardFocus(args); Render(); }
}
