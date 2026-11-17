using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
namespace WinSwitch.Apps;

// "Switching to Out and about": a glass panel above the clock with a progress line, and a small Close / Keep prompt for each app with a window (SPEC.md › Apps to stop)
public sealed class SwitchPanel : Window
{
    static readonly Brush lightBrush = new SolidColorBrush(Color.FromRgb(0xF2, 0xF4, 0xF7)), softBrush = new SolidColorBrush(Color.FromRgb(0xC9, 0xD0, 0xDA)), quietBrush = new SolidColorBrush(Color.FromRgb(0xB8, 0xC0, 0xCC)), accentBrush = new SolidColorBrush(Color.FromRgb(0x2F, 0x7F, 0xD8)), glassButtonBrush = new SolidColorBrush(Color.FromArgb(0x2E, 0xFF, 0xFF, 0xFF)), okBrush = new SolidColorBrush(Color.FromRgb(0x58, 0xE2, 0x8C)), warnBrush = new SolidColorBrush(Color.FromRgb(0xF0, 0xB4, 0x5A));
    readonly TextBlock titleText, summaryText;
    readonly StackPanel rows = new();
    readonly Border progressTrack, closeAllButton;
    readonly WrapPanel footer = new() { Margin = new Thickness(0, 10, 0, 0) };
    readonly List<Row> prompts = new();
    readonly DispatcherTimer fadeTimer = new() { Interval = TimeSpan.FromSeconds(6) };
    Action? whenSettled;
    bool finished, isClosed;

    public SwitchPanel(string profileName)
    {
        WindowStyle = WindowStyle.None; AllowsTransparency = true; Background = Brushes.Transparent; Topmost = true; ShowInTaskbar = false; ShowActivated = false; ResizeMode = ResizeMode.NoResize;
        Width = 412; SizeToContent = SizeToContent.Height; FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");
        var close = new TextBlock { Text = "✕", Foreground = quietBrush, FontSize = 13, Cursor = Cursors.Hand, Padding = new Thickness(6, 0, 0, 0) };
        close.MouseLeftButtonUp += (sender, args) => KeepAllAndClose();
        DockPanel.SetDock(close, Dock.Right);
        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 6), Children = { close, new StackPanel { Orientation = Orientation.Horizontal, Children = { new Border { Width = 8, Height = 8, CornerRadius = new CornerRadius(4), Background = okBrush, Margin = new Thickness(0, 0, 7, 0), VerticalAlignment = VerticalAlignment.Center }, new TextBlock { Text = "WinSwitch", Foreground = quietBrush, FontSize = 11 } } } } };
        titleText = new TextBlock { Text = $"Switching to {profileName}", Foreground = lightBrush, FontSize = 14.5, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
        summaryText = new TextBlock { Foreground = softBrush, FontSize = 12.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 0), Visibility = Visibility.Collapsed };
        // A thin sliding progress line while anything is still happening
        var runner = new Border { Width = 90, Height = 3, CornerRadius = new CornerRadius(1.5), Background = accentBrush, HorizontalAlignment = HorizontalAlignment.Left, RenderTransform = new TranslateTransform() };
        progressTrack = new Border { Height = 3, CornerRadius = new CornerRadius(1.5), Background = new SolidColorBrush(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF)), Margin = new Thickness(0, 9, 0, 6), ClipToBounds = true, Child = runner };
        runner.RenderTransform.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(-90, 380, TimeSpan.FromSeconds(1.3)) { RepeatBehavior = RepeatBehavior.Forever });
        closeAllButton = GlassButton("Close all", true, () => { foreach (var prompt in prompts.Where(prompt => prompt.Pending).ToList()) prompt.Choose(true); });
        closeAllButton.Visibility = Visibility.Collapsed; closeAllButton.Margin = new Thickness(0);
        footer.Children.Add(closeAllButton);
        var content = new StackPanel { Children = { header, titleText, summaryText, progressTrack, rows, footer } };
        Content = new Border { Background = new SolidColorBrush(Color.FromArgb(0xEE, 0x20, 0x24, 0x2C)), BorderBrush = new SolidColorBrush(Color.FromArgb(0x24, 0xFF, 0xFF, 0xFF)), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10), Padding = new Thickness(14, 12, 14, 12), Margin = new Thickness(10), Child = content, Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 24, ShadowDepth = 6, Opacity = 0.4 } };
        Closed += (sender, args) => { isClosed = true; fadeTimer.Stop(); };
        fadeTimer.Tick += (sender, args) => { if (!IsMouseOver) { fadeTimer.Stop(); FadeAndClose(); } };
        SizeChanged += (sender, args) => Place();
        Loaded += (sender, args) => { Opacity = 0; Place(); BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(180))); };
    }

    // Bottom right of the main screen's work area, growing upwards as rows are added
    void Place() { var area = SystemParameters.WorkArea; Left = area.Right - Width - 6; Top = area.Bottom - ActualHeight - 6; }

    public Row AddLine(string path, string text) { var row = new Row(this, path); row.Working(text); rows.Children.Add(row.Element); return row; }

    public void AddPrompt(string path, string question, Func<Row, Task> onClose, Action<Row> onKeep)
    {
        var row = new Row(this, path);
        row.Ask(question, ("Close", () => onClose(row)), ("Keep", () => { onKeep(row); return Task.CompletedTask; }));
        prompts.Add(row);
        rows.Children.Add(row.Element);
        UpdateCloseAll();
    }

    void UpdateCloseAll() => closeAllButton.Visibility = prompts.Count(prompt => prompt.Pending) >= 2 ? Visibility.Visible : Visibility.Collapsed;

    // Called when the background work is done; the summary waits for any open questions
    public void WhenSettled(Action action) { whenSettled = action; CheckSettled(); }
    void CheckSettled()
    {
        UpdateCloseAll();
        if (whenSettled == null || rows.Children.Count > 0 && AllRows().Any(row => row.Pending || row.Busy)) return;
        var action = whenSettled; whenSettled = null; action();
    }
    IEnumerable<Row> AllRows() => rows.Children.OfType<FrameworkElement>().Select(element => element.Tag).OfType<Row>();

    public void Finish(string summary, Action? startAgain)
    {
        if (isClosed) return;
        finished = true;
        titleText.Text = titleText.Text.Replace("Switching to", "Switched to");
        progressTrack.Visibility = Visibility.Collapsed;
        if (summary != "") { summaryText.Text = summary + "."; summaryText.Visibility = Visibility.Visible; }
        footer.Children.Clear();
        if (startAgain != null) { var link = new TextBlock { Text = "Start them now", Foreground = new SolidColorBrush(Color.FromRgb(0x8C, 0xC0, 0xFF)), FontWeight = FontWeights.SemiBold, FontSize = 12.5, Cursor = Cursors.Hand }; link.MouseLeftButtonUp += (sender, args) => { startAgain(); FadeAndClose(); }; footer.Children.Add(link); }
        fadeTimer.Start();
    }

    // × or a newer switch: anything still asking is kept
    public void KeepAllAndClose()
    {
        foreach (var prompt in prompts.Where(prompt => prompt.Pending).ToList()) prompt.Choose(false);
        if (!isClosed) Close();
    }

    void FadeAndClose() { if (isClosed) return; var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(300)); fade.Completed += (sender, args) => { if (!isClosed) Close(); }; BeginAnimation(OpacityProperty, fade); }

    static Border GlassButton(string label, bool primary, Action action)
    {
        var button = new Border { Background = primary ? accentBrush : glassButtonBrush, CornerRadius = new CornerRadius(6), Padding = new Thickness(12, 4, 12, 5), Margin = new Thickness(6, 0, 0, 0), Cursor = Cursors.Hand, VerticalAlignment = VerticalAlignment.Center, Child = new TextBlock { Text = label, Foreground = lightBrush, FontSize = 12, FontWeight = primary ? FontWeights.SemiBold : FontWeights.Normal } };
        button.MouseLeftButtonUp += (sender, args) => action();
        return button;
    }

    // One app on the panel: its icon, a line of text, and buttons while it asks
    public sealed class Row
    {
        readonly SwitchPanel panel;
        readonly TextBlock text = new() { Foreground = lightBrush, FontSize = 12.5, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
        readonly TextBlock mark = new() { FontSize = 12, Width = 16, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) };
        readonly StackPanel buttons = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        List<(string label, Func<Task> action)> choices = new();
        public FrameworkElement Element { get; }
        public bool Pending { get; private set; }
        public bool Busy { get; private set; }

        public Row(SwitchPanel panel, string path)
        {
            this.panel = panel;
            var icon = new Image { Width = 20, Height = 20, Source = AppIcon.For(path), Margin = new Thickness(0, 0, 9, 0), VerticalAlignment = VerticalAlignment.Center };
            DockPanel.SetDock(icon, Dock.Left); DockPanel.SetDock(buttons, Dock.Right); DockPanel.SetDock(mark, Dock.Right);
            Element = new Border { Background = new SolidColorBrush(Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF)), CornerRadius = new CornerRadius(7), Padding = new Thickness(9, 7, 9, 7), Margin = new Thickness(0, 6, 0, 0), Child = new DockPanel { Children = { icon, buttons, mark, text } }, Tag = this };
        }

        public void Working(string line) { text.Text = line; text.Foreground = softBrush; mark.Text = ""; buttons.Children.Clear(); Pending = false; Busy = true; }
        public void Ask(string question, params (string label, Func<Task> action)[] options)
        {
            text.Text = question; text.Foreground = lightBrush; mark.Text = ""; Pending = true; Busy = false; choices = options.ToList();
            buttons.Children.Clear();
            for (var index = 0; index < options.Length; index++) { var choice = index; buttons.Children.Add(GlassButton(options[index].label, index == 0, () => Choose(choice == 0))); }
            panel.UpdateCloseAll();
        }
        // true = the first button (Close / End it), false = the last (Keep / Leave it)
        public void Choose(bool first)
        {
            if (!Pending || choices.Count == 0) return;
            Pending = false; buttons.Children.Clear();
            var action = first ? choices[0].action : choices[^1].action;
            _ = Run(action);
        }
        async Task Run(Func<Task> action) { try { await action(); } catch (Exception error) { Shell.CrashReporter.Write("Switching panel", error, false); Done("Something went wrong", false); } }
        public void Done(string line, bool ok)
        {
            text.Text = line; text.Foreground = softBrush; mark.Text = ok ? "✓" : "!"; mark.Foreground = ok ? okBrush : warnBrush; buttons.Children.Clear(); Pending = false; Busy = false;
            panel.CheckSettled();
            if (panel.finished && !panel.isClosed) panel.fadeTimer.Start();
        }
    }
}

// Program icons for the panel and the chooser, read once per path
public static class AppIcon
{
    static readonly Dictionary<string, ImageSource?> cache = new(StringComparer.OrdinalIgnoreCase);
    public static ImageSource? For(string path)
    {
        if (cache.TryGetValue(path, out var cached)) return cached;
        ImageSource? image = null;
        try
        {
            using var icon = System.Drawing.Icon.ExtractAssociatedIcon(path);
            if (icon != null) { image = Imaging.CreateBitmapSourceFromHIcon(icon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions()); image.Freeze(); }
        }
        catch { }
        return cache[path] = image;
    }
}
