using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
namespace WinSwitch.Shell;

// WinSwitch's own small cards above the clock, never Windows notifications (SPEC.md › Notifications).
// Info fades after 5 s with an optional Undo; a question stays until answered, and one closed with × waits in the tray menu
public sealed class Notices
{
    public record Question(string Key, string Title, string Text, List<(string label, Action action)> Buttons, Func<bool>? StillRelevant);
    readonly List<Question> waiting = new(); // asked but not yet shown (one at a time, or held while quiet)
    public List<Question> Unanswered { get; } = new(); // closed with ×: kept in the tray menu
    NoticeCard? infoCard, questionCard;
    Question? showing;
    readonly DispatcherTimer quietTimer = new() { Interval = TimeSpan.FromSeconds(20) };
    public event Action? Changed;
    App App => App.Instance;
    // The tray dot, its menu row and the card share one colour, so a coloured dot always leads to a card of the same colour (Mat 18 Nov)
    public const string QuestionColour = "#E0912F", UpdateColour = "#4CC2FF", PlainColour = "#58E28C";

    readonly DispatcherTimer relevanceTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    string withdrawing = "";

    public Notices()
    {
        quietTimer.Tick += (sender, args) => ShowNextQuestion(); quietTimer.Start();
        // A question whose reason has gone (unplugged again, changed back, window closed) is withdrawn: its card closes and the amber dot stops (Mat 18 Nov)
        relevanceTimer.Tick += (sender, args) =>
        {
            var questions = waiting.Concat(Unanswered).Append(showing).OfType<Question>().Where(question => question.StillRelevant != null).ToList();
            foreach (var question in questions) { bool relevant; try { relevant = question.StillRelevant!(); } catch { relevant = true; } if (!relevant) Withdraw(question.Key); }
        };
        relevanceTimer.Start();
    }

    public void Withdraw(string key)
    {
        var removed = waiting.RemoveAll(question => question.Key == key) + Unanswered.RemoveAll(question => question.Key == key);
        if (showing?.Key == key && questionCard != null) { withdrawing = key; questionCard.Close(); withdrawing = ""; removed++; }
        if (removed > 0) Changed?.Invoke();
    }

    public void Info(string title, string text, Action? undo = null, string actionLabel = "Undo", bool always = false, string accent = PlainColour)
    {
        if (!always && App.Settings.General.Notifications != "all" || IsQuiet()) return;
        infoCard?.Close();
        infoCard = new NoticeCard(title, text, undo == null ? new() : new() { (actionLabel, undo) }, isQuestion: false, accent);
        infoCard.Closed += (sender, args) => { if (sender == infoCard) infoCard = null; };
        infoCard.ShowAt(questionCard?.ActualHeight + 10 ?? 0);
    }

    public void Ask(string key, string title, string text, List<(string label, Action action)> buttons, Func<bool>? stillRelevant = null)
    {
        waiting.RemoveAll(question => question.Key == key); Unanswered.RemoveAll(question => question.Key == key);
        if (showing?.Key == key) return;
        waiting.Add(new Question(key, title, text, buttons, stillRelevant));
        ShowNextQuestion();
    }

    // Asked for by a click (tray icon or Show message): shown even in Do not disturb or full screen, so the click never does nothing
    public void ShowUnanswered() { if (Unanswered.Count == 0) return; var next = Unanswered[0]; Unanswered.RemoveAt(0); waiting.Insert(0, next); Changed?.Invoke(); ShowNextQuestion(clicked: true); }

    void ShowNextQuestion() => ShowNextQuestion(false);
    void ShowNextQuestion(bool clicked)
    {
        if (showing != null || waiting.Count == 0 || !clicked && IsQuiet()) return;
        var question = showing = waiting[0]; waiting.RemoveAt(0);
        var answered = false;
        questionCard = new NoticeCard(question.Title, question.Text, question.Buttons.Select(button => (button.label, (Action)(() => { answered = true; try { button.action(); } catch (Exception error) { CrashReporter.Write("Answering a question", error, false); } }))).ToList(), isQuestion: true, QuestionColour);
        questionCard.Closed += (sender, args) =>
        {
            if (!answered && withdrawing != question.Key) Unanswered.Add(question);
            showing = null; questionCard = null; Changed?.Invoke();
            App.Dispatcher.BeginInvoke(ShowNextQuestion);
        };
        questionCard.ShowAt(0);
        Changed?.Invoke();
    }

    // Full-screen apps, presentations and Do not disturb: info cards are skipped, questions wait
    static bool IsQuiet() { try { return SHQueryUserNotificationState(out var state) == 0 && state is 2 or 3 or 4 or 6; } catch { return false; } }
    [DllImport("shell32.dll")] static extern int SHQueryUserNotificationState(out int state);
}

// One card: dark glass, bottom right above the taskbar, slides up; never takes the keyboard
public sealed class NoticeCard : Window
{
    readonly bool isQuestion;
    readonly DispatcherTimer fadeTimer = new() { Interval = TimeSpan.FromSeconds(5) };

    public NoticeCard(string title, string text, List<(string label, Action action)> buttons, bool isQuestion, string accent)
    {
        var accentColour = (Color)ColorConverter.ConvertFromString(accent); var accentBrush = new SolidColorBrush(accentColour);
        this.isQuestion = isQuestion;
        WindowStyle = WindowStyle.None; AllowsTransparency = true; Background = Brushes.Transparent; Topmost = true; ShowInTaskbar = false; ShowActivated = false; ResizeMode = ResizeMode.NoResize;
        Width = 372; SizeToContent = SizeToContent.Height; FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");
        var light = new SolidColorBrush(Color.FromRgb(0xF2, 0xF4, 0xF7)); var quiet = new SolidColorBrush(Color.FromRgb(0xB8, 0xC0, 0xCC));
        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
        if (isQuestion) { var close = new TextBlock { Text = "✕", Foreground = quiet, FontSize = 13, Cursor = Cursors.Hand, Padding = new Thickness(6, 0, 0, 0) }; close.MouseLeftButtonUp += (sender, args) => Close(); DockPanel.SetDock(close, Dock.Right); header.Children.Add(close); }
        header.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Children = { Led(accent), new TextBlock { Text = isQuestion ? "WinSwitch · Message" : "WinSwitch", Foreground = isQuestion ? accentBrush : quiet, FontSize = 11 } } });
        var body = new StackPanel();
        body.Children.Add(new TextBlock { Text = title, Foreground = light, FontSize = 14, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        if (text != "") body.Children.Add(new TextBlock { Text = text, Foreground = new SolidColorBrush(Color.FromRgb(0xC9, 0xD0, 0xDA)), FontSize = 12.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 0) });
        var content = new StackPanel { Children = { header } };
        if (isQuestion)
        {
            content.Children.Add(body);
            var row = new UniformGrid { Rows = 1, Margin = new Thickness(0, 11, 0, 0) };
            for (var index = 0; index < buttons.Count; index++)
            {
                var (label, action) = buttons[index];
                var button = new Border { Background = index == 0 ? accentBrush : new SolidColorBrush(Color.FromArgb(0x2E, 0xFF, 0xFF, 0xFF)), CornerRadius = new CornerRadius(6), Padding = new Thickness(6, 6, 6, 6), Margin = new Thickness(index == 0 ? 0 : 4, 0, index == buttons.Count - 1 ? 0 : 4, 0), Cursor = Cursors.Hand, Child = new TextBlock { Text = label, Foreground = index == 0 ? new SolidColorBrush(Color.FromRgb(0x1A, 0x1C, 0x20)) : light, FontSize = 12, FontWeight = index == 0 ? FontWeights.SemiBold : FontWeights.Normal, HorizontalAlignment = HorizontalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis } };
                button.MouseLeftButtonUp += (sender, args) => { action(); Close(); };
                row.Children.Add(button);
            }
            content.Children.Add(row);
        }
        else
        {
            var line = new DockPanel();
            foreach (var (label, action) in buttons) { var link = new TextBlock { Text = label, Foreground = new SolidColorBrush(Color.FromRgb(0x8C, 0xC0, 0xFF)), FontWeight = FontWeights.SemiBold, FontSize = 12.5, Cursor = Cursors.Hand, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) }; link.MouseLeftButtonUp += (sender, args) => { action(); Close(); }; DockPanel.SetDock(link, Dock.Right); line.Children.Add(link); }
            line.Children.Add(body);
            content.Children.Add(line);
            fadeTimer.Tick += (sender, args) => { if (!IsMouseOver) { fadeTimer.Stop(); FadeAndClose(); } };
        }
        Content = new Border { Background = new SolidColorBrush(Color.FromArgb(0xEE, 0x20, 0x24, 0x2C)), BorderBrush = accent == Notices.PlainColour ? new SolidColorBrush(Color.FromArgb(0x24, 0xFF, 0xFF, 0xFF)) : new SolidColorBrush(Color.FromArgb(0x99, accentColour.R, accentColour.G, accentColour.B)), BorderThickness = new Thickness(accent == Notices.PlainColour ? 1 : 1.5), CornerRadius = new CornerRadius(10), Padding = new Thickness(14, 12, 14, 12), Margin = new Thickness(10), Child = content, Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 24, ShadowDepth = 6, Opacity = 0.4 } };
    }

    // The header LED. Message (amber) and update (blue) pulse like WinSqueeze's hi-fi standby LED (Mat 18 Nov: the ideal; the tray dot only approximates it):
    // a lit glass bead (light highlight top left, deep edge) whose glow swells and brightens every 1.6 s. Plain cards keep a still green dot
    static FrameworkElement Led(string accent)
    {
        var led = new System.Windows.Shapes.Ellipse { Width = 9, Height = 9, Margin = new Thickness(0, 0, 7, 0), VerticalAlignment = VerticalAlignment.Center };
        var colour = (Color)ColorConverter.ConvertFromString(accent);
        if (accent == Notices.PlainColour) { led.Fill = new SolidColorBrush(colour); return led; }
        Color Shade(double amount) => Color.FromRgb((byte)(colour.R * amount), (byte)(colour.G * amount), (byte)(colour.B * amount));
        Color Tint(double amount) => Color.FromRgb((byte)(colour.R + (255 - colour.R) * amount), (byte)(colour.G + (255 - colour.G) * amount), (byte)(colour.B + (255 - colour.B) * amount));
        led.Fill = new RadialGradientBrush { GradientOrigin = new Point(0.4, 0.35), Center = new Point(0.4, 0.35), RadiusX = 0.7, RadiusY = 0.7, GradientStops = { new GradientStop(Tint(0.8), 0), new GradientStop(colour, 0.45), new GradientStop(Shade(0.35), 1) } };
        var glow = new System.Windows.Media.Effects.DropShadowEffect { Color = colour, ShadowDepth = 0, BlurRadius = 3, Opacity = 0.35 };
        led.Effect = glow;
        var ease = new SineEase { EasingMode = EasingMode.EaseInOut }; var half = TimeSpan.FromSeconds(0.8);
        glow.BeginAnimation(System.Windows.Media.Effects.DropShadowEffect.BlurRadiusProperty, new DoubleAnimation(3, 12, half) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = ease });
        glow.BeginAnimation(System.Windows.Media.Effects.DropShadowEffect.OpacityProperty, new DoubleAnimation(0.35, 0.9, half) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = ease });
        led.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0.7, 1, half) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = ease });
        return led;
    }

    // Bottom right of the main screen's work area, lifted by `above` px when another card is there
    public void ShowAt(double above)
    {
        Opacity = 0; Show();
        var corner = Corner.BottomRight(this);
        Left = corner.X - Width - 6;
        var finalTop = corner.Y - ActualHeight - 6 - above;
        Top = finalTop + 18;
        BeginAnimation(TopProperty, new DoubleAnimation(finalTop, TimeSpan.FromMilliseconds(220)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(180)));
        if (!isQuestion) fadeTimer.Start();
    }

    void FadeAndClose() { var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(300)); fade.Completed += (sender, args) => Close(); BeginAnimation(OpacityProperty, fade); }
}
