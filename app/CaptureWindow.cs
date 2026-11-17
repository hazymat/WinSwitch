using System.Collections.Concurrent;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using WinSwitch.Power;
namespace WinSwitch;

// First run: "Saving how this PC is set up". Sections tick on the left while what's read scrolls past on the right like a console;
// afterwards each section, or the full list as a tree, can be looked through. Also opens later from Original settings (Mat 17 Nov)
public sealed class CaptureWindow : Window
{
    static readonly Brush quietBrush = new SolidColorBrush(Color.FromRgb(0x66, 0x6B, 0x75)), accentBrush = new SolidColorBrush(Color.FromRgb(0x2F, 0x7F, 0xD8)), okBrush = new SolidColorBrush(Color.FromRgb(0x1D, 0x8A, 0x45)), consoleBack = new SolidColorBrush(Color.FromRgb(0x16, 0x19, 0x1F)), consoleText = new SolidColorBrush(Color.FromRgb(0xB9, 0xE8, 0xC4)), consoleGroup = new SolidColorBrush(Color.FromRgb(0x7F, 0xB4, 0xFF)), consoleDim = new SolidColorBrush(Color.FromRgb(0x6C, 0x76, 0x84));
    readonly ConcurrentQueue<(string section, string group, string line)> arriving = new();
    readonly Dictionary<string, (TextBlock mark, TextBlock count, Border item)> sectionItems = new();
    readonly Dictionary<string, List<(string group, string line)>> shown = new();
    readonly ListBox console = new() { Background = consoleBack, BorderThickness = new Thickness(0), FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 12, Padding = new Thickness(8) };
    readonly TreeView fullTree = new() { BorderThickness = new Thickness(0), FontSize = 12.5, Padding = new Thickness(6), Visibility = Visibility.Collapsed };
    readonly TextBlock titleText = new() { FontSize = 22, FontWeight = FontWeights.SemiBold }, statusText = new() { Foreground = quietBrush, VerticalAlignment = VerticalAlignment.Center, FontSize = 12.5 };
    readonly Button continueButton = new() { Content = "Continue", FontWeight = FontWeights.SemiBold, Padding = new Thickness(22, 6, 22, 6), IsEnabled = false, IsDefault = true };
    readonly DispatcherTimer pace = new() { Interval = TimeSpan.FromMilliseconds(16) };
    readonly StackPanel sectionList = new() { Margin = new Thickness(8, 10, 8, 10) };
    string view = "log", lastSection = "";
    bool readingDone;
    int total;

    // Live: reads the PC now. With a snapshot: shows what was saved then
    public CaptureWindow(Snapshot? saved = null)
    {
        Title = "WinSwitch"; Width = 1040; Height = 680; MinWidth = 760; MinHeight = 480; WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Icon = new System.Windows.Media.Imaging.BitmapImage(new Uri("pack://application:,,,/Assets/WinSwitch.ico"));
        Background = new SolidColorBrush(Color.FromRgb(0xF7, 0xF8, 0xFA)); FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");
        // Top: what this is and why
        var note = new Border { Background = new SolidColorBrush(Color.FromRgb(0xEE, 0xF4, 0xFC)), BorderBrush = new SolidColorBrush(Color.FromRgb(0xD3, 0xE2, 0xF5)), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Padding = new Thickness(12, 9, 12, 9), Margin = new Thickness(0, 8, 0, 0),
            Child = new DockPanel { Children = { new TextBlock { FontFamily = new FontFamily("Segoe MDL2 Assets"), Text = "", Foreground = accentBrush, FontSize = 15, Margin = new Thickness(0, 1, 10, 0) }, new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = new SolidColorBrush(Color.FromRgb(0x2B, 0x3A, 0x4E)), LineHeight = 19, Text = "Before it changes anything, WinSwitch saves how this PC is set up now. If you ever want to undo its changes, or you uninstall it, everything is put back exactly as it was. Apps, startup items, scheduled tasks and services are recorded too, so anything WinSwitch stops can be started again the same way." } } } };
        var top = new StackPanel { Margin = new Thickness(20, 16, 20, 12), Children = { titleText, note } };
        // Left: the sections, then the full list
        sectionList.Children.Add(Item("log", "", "Capture log", null));
        foreach (var section in Snapshot.SectionTitles) { shown[section] = new(); sectionList.Children.Add(Item(section, "", section, section)); }
        sectionList.Children.Add(new Border { Height = 1, Background = new SolidColorBrush(Color.FromRgb(0xE2, 0xE2, 0xE2)), Margin = new Thickness(6, 8, 6, 8) });
        sectionList.Children.Add(Item("full", "", "Full list", null));
        var left = new Border { Background = new SolidColorBrush(Color.FromRgb(0xF3, 0xF3, 0xF3)), BorderBrush = new SolidColorBrush(Color.FromRgb(0xE2, 0xE2, 0xE2)), BorderThickness = new Thickness(0, 1, 1, 1), Width = 270, Child = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = sectionList } };
        VirtualizingPanel.SetIsVirtualizing(console, true); ScrollViewer.SetHorizontalScrollBarVisibility(console, ScrollBarVisibility.Disabled);
        var right = new Border { BorderBrush = new SolidColorBrush(Color.FromRgb(0xE2, 0xE2, 0xE2)), BorderThickness = new Thickness(0, 1, 0, 1), Child = new Grid { Children = { console, fullTree } } };
        var middle = new DockPanel { Children = { left, right } }; DockPanel.SetDock(left, Dock.Left);
        // Bottom: status and Continue
        var bottom = new DockPanel { Margin = new Thickness(20, 12, 20, 14), Children = { continueButton, statusText } }; DockPanel.SetDock(continueButton, Dock.Right);
        continueButton.Click += (sender, args) => Close();
        var root = new DockPanel(); DockPanel.SetDock(top, Dock.Top); DockPanel.SetDock(bottom, Dock.Bottom);
        root.Children.Add(top); root.Children.Add(bottom); root.Children.Add(middle);
        Content = root;
        pace.Tick += (sender, args) => ShowArrived();
        if (saved == null) { titleText.Text = "Saving how this PC is set up"; statusText.Text = "Reading…"; }
        else
        {
            titleText.Text = $"What WinSwitch saved on {saved.TakenAt:d MMMM yyyy}"; continueButton.Content = "Close"; continueButton.IsEnabled = true;
            foreach (var section in saved.Sections) foreach (var line in section.Lines) if (shown.ContainsKey(section.Title)) arriving.Enqueue((section.Title, line.Group, line.Text));
            readingDone = true; pace.Interval = TimeSpan.FromMilliseconds(1); ShowArrived(all: true); Select("full");
        }
    }

    // Reads everything (off the UI thread) while the screen shows it arriving; the window stays until Continue
    public async Task<Snapshot> CaptureAsync()
    {
        var stopwatch = Stopwatch.StartNew();
        pace.Start();
        var snapshot = await Task.Run(() => Snapshot.TakeAsync((section, group, line) => arriving.Enqueue((section, group, line))));
        readingDone = true;
        var seconds = stopwatch.Elapsed.TotalSeconds;
        finishedText = $"Read {snapshot.Sections.Sum(section => section.Lines.Count):N0} things in {seconds:0.0} seconds and saved them in Original settings.";
        return snapshot;
    }
    string finishedText = "";

    // A few lines per frame, so it visibly scrolls even when reading takes a second
    void ShowArrived(bool all = false)
    {
        var take = all ? int.MaxValue : Math.Max(2, arriving.Count / 70);
        while (take-- > 0 && arriving.TryDequeue(out var entry))
        {
            if (entry.section != lastSection) { if (lastSection != "") Tick(lastSection, true); lastSection = entry.section; Tick(entry.section, false); if (!all && view == "log") console.Items.Add(Line($"── {entry.section} ──", consoleGroup)); }
            shown[entry.section].Add((entry.group, entry.line)); total++;
            sectionItems[entry.section].count.Text = shown[entry.section].Count.ToString("N0");
            if (!all && view == "log") console.Items.Add(Line((entry.group == "" ? "" : entry.group + "  ›  ") + entry.line, consoleText));
            if (!all) statusText.Text = $"Reading {entry.section.ToLowerInvariant()}…  {total:N0} things so far";
        }
        if (view == "log" && console.Items.Count > 0) console.ScrollIntoView(console.Items[^1]);
        if (readingDone && arriving.IsEmpty)
        {
            pace.Stop();
            foreach (var section in Snapshot.SectionTitles) Tick(section, true);
            if (!all) { titleText.Text = "Saved: this is how your PC was set up"; statusText.Text = finishedText; continueButton.IsEnabled = true; continueButton.Focus(); if (view == "log") { console.Items.Add(Line("── Done ──", consoleGroup)); console.ScrollIntoView(console.Items[^1]); } }
        }
    }

    void Tick(string section, bool done)
    {
        var (mark, count, item) = sectionItems[section];
        mark.Text = done ? "" : ""; mark.Foreground = done ? okBrush : accentBrush;
        if (done && shown[section].Count == 0) count.Text = "none";
    }

    Border Item(string key, string glyph, string label, string? section)
    {
        var mark = new TextBlock { FontFamily = new FontFamily("Segoe MDL2 Assets"), Text = glyph == "" ? "" : glyph, Foreground = glyph == "" ? new SolidColorBrush(Color.FromRgb(0xB0, 0xB6, 0xC0)) : quietBrush, FontSize = 13, Width = 22, VerticalAlignment = VerticalAlignment.Center };
        var count = new TextBlock { Foreground = quietBrush, FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(mark, Dock.Left); DockPanel.SetDock(count, Dock.Right);
        var item = new Border { CornerRadius = new CornerRadius(4), Padding = new Thickness(8, 6, 8, 6), Margin = new Thickness(0, 1, 0, 1), Cursor = Cursors.Hand, Background = key == view ? Brushes.White : Brushes.Transparent, Tag = key, Child = new DockPanel { Children = { mark, count, new TextBlock { Text = label, FontSize = 13, VerticalAlignment = VerticalAlignment.Center } } } };
        item.MouseLeftButtonUp += (sender, args) => Select(key);
        if (section != null) sectionItems[section] = (mark, count, item);
        return item;
    }

    // Capture log, one section's findings, or everything as a tree
    void Select(string key)
    {
        view = key;
        foreach (var item in sectionList.Children.OfType<Border>().Where(item => item.Tag is string)) item.Background = (string)item.Tag == key ? Brushes.White : Brushes.Transparent;
        console.Visibility = key == "full" ? Visibility.Collapsed : Visibility.Visible; fullTree.Visibility = key == "full" ? Visibility.Visible : Visibility.Collapsed;
        if (key == "full") { FillTree(); return; }
        if (key == "log") { console.ItemsSource = null; RebuildLog(); return; }
        console.Items.Clear();
        foreach (var group in shown[key].GroupBy(entry => entry.group))
        {
            if (group.Key != "") console.Items.Add(Line(group.Key, consoleGroup));
            foreach (var entry in group) console.Items.Add(Line((group.Key == "" ? "" : "    ") + entry.line, consoleText));
        }
        if (shown[key].Count == 0) console.Items.Add(Line(readingDone ? "Nothing found." : "Not read yet…", consoleDim));
    }

    void RebuildLog()
    {
        console.Items.Clear();
        foreach (var section in Snapshot.SectionTitles.Where(section => shown[section].Count > 0))
        {
            console.Items.Add(Line($"── {section} ──", consoleGroup));
            foreach (var (group, line) in shown[section]) console.Items.Add(Line((group == "" ? "" : group + "  ›  ") + line, consoleText));
        }
        if (console.Items.Count > 0) console.ScrollIntoView(console.Items[^1]);
    }

    void FillTree()
    {
        fullTree.Items.Clear();
        foreach (var section in Snapshot.SectionTitles)
        {
            var sectionNode = new TreeViewItem { Header = $"{section}  ({shown[section].Count:N0})", FontWeight = FontWeights.SemiBold, IsExpanded = true };
            foreach (var group in shown[section].GroupBy(entry => entry.group))
            {
                ItemsControl parent = sectionNode;
                if (group.Key != "") { var groupNode = new TreeViewItem { Header = group.Key, FontWeight = FontWeights.SemiBold, IsExpanded = group.Count() <= 3 }; sectionNode.Items.Add(groupNode); parent = groupNode; }
                foreach (var entry in group) parent.Items.Add(new TreeViewItem { Header = entry.line, FontWeight = FontWeights.Normal });
            }
            fullTree.Items.Add(sectionNode);
        }
    }

    static TextBlock Line(string text, Brush colour) => new() { Text = text, Foreground = colour, TextWrapping = TextWrapping.Wrap };
}
