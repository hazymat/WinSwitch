using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using WinSwitch.Settings;
namespace WinSwitch.Apps;

// Add apps to stop: one combined list with a search box, Running now (windows first) then Starts with Windows, plus Browse (SPEC.md › Apps to stop)
public sealed class AppPicker : Window
{
    static readonly Brush quietBrush = new SolidColorBrush(Color.FromRgb(0x66, 0x6B, 0x75)), accentBrush = new SolidColorBrush(Color.FromRgb(0x2F, 0x7F, 0xD8));
    readonly TextBox searchBox = new() { FontSize = 14, Padding = new Thickness(8, 5, 8, 5), Margin = new Thickness(0, 0, 0, 10) };
    readonly StackPanel listPanel = new();
    readonly HashSet<string> alreadyListed, picked = new(StringComparer.OrdinalIgnoreCase);
    readonly Button addButton = new() { Content = "Add", FontWeight = FontWeights.SemiBold, Padding = new Thickness(16, 4, 16, 4), IsDefault = true, IsEnabled = false };
    List<AppCatalog.Choice> choices = new();
    public List<AppToStop> Chosen { get; } = new();

    public AppPicker(IEnumerable<string> listed)
    {
        alreadyListed = new HashSet<string>(listed, StringComparer.OrdinalIgnoreCase);
        Title = "Add apps to stop"; Width = 620; Height = 640; MinWidth = 480; MinHeight = 400; WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;
        Background = new SolidColorBrush(Color.FromRgb(0xF7, 0xF8, 0xFA)); FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");
        var browseButton = new Button { Content = "Browse for a program…", Padding = new Thickness(12, 4, 12, 4) };
        browseButton.Click += (sender, args) => Browse();
        var cancelButton = new Button { Content = "Cancel", Padding = new Thickness(14, 4, 14, 4), Margin = new Thickness(8, 0, 0, 0), IsCancel = true };
        // One choice for everything added this time (Mat 17 Nov: changing each to On battery was tedious)
        var whenBox = new ComboBox { Width = 150, Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center, Items = { new ComboBoxItem { Content = "Stop always" }, new ComboBoxItem { Content = "Stop on battery" } }, SelectedIndex = 1 };
        addButton.Click += (sender, args) => { foreach (var path in picked) Chosen.Add(new AppToStop { Path = path, Name = choices.FirstOrDefault(choice => choice.Path.Equals(path, StringComparison.OrdinalIgnoreCase))?.Name ?? AppCatalog.FriendlyName(path), OnBatteryOnly = whenBox.SelectedIndex == 1 }); DialogResult = true; };
        var buttons = new DockPanel { Margin = new Thickness(0, 12, 0, 0), Children = { browseButton } };
        var right = new StackPanel { Orientation = Orientation.Horizontal, Children = { whenBox, addButton, cancelButton } };
        DockPanel.SetDock(right, Dock.Right); buttons.Children.Insert(0, right); DockPanel.SetDock(browseButton, Dock.Left);
        var hint = new TextBlock { Text = "Background apps are stopped straight away; apps with a window ask first. They start again when you switch to a profile that doesn't stop them.", Foreground = quietBrush, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10) };
        var root = new DockPanel { Margin = new Thickness(16) };
        DockPanel.SetDock(searchBox, Dock.Top); DockPanel.SetDock(hint, Dock.Top); DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(searchBox); root.Children.Add(hint); root.Children.Add(buttons);
        root.Children.Add(new Border { Background = Brushes.White, BorderBrush = new SolidColorBrush(Color.FromRgb(0xE3, 0xE5, 0xE8)), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Child = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = listPanel, Padding = new Thickness(4) } });
        Content = root;
        listPanel.Children.Add(new TextBlock { Text = "Looking at what's running and what starts with Windows…", Foreground = quietBrush, Margin = new Thickness(10) });
        searchBox.TextChanged += (sender, args) => Fill();
        Loaded += async (sender, args) => { searchBox.Focus(); choices = await Task.Run(AppCatalog.Choices); Fill(); };
    }

    void Fill()
    {
        listPanel.Children.Clear();
        var words = searchBox.Text.Trim();
        var shown = choices.Where(choice => words == "" || choice.Name.Contains(words, StringComparison.OrdinalIgnoreCase) || choice.Path.Contains(words, StringComparison.OrdinalIgnoreCase)).ToList();
        foreach (var group in shown.GroupBy(choice => choice.Group))
        {
            listPanel.Children.Add(new TextBlock { Text = group.Key.ToUpperInvariant(), FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = quietBrush, Margin = new Thickness(10, 12, 0, 4) });
            foreach (var choice in group) listPanel.Children.Add(RowFor(choice));
        }
        if (shown.Count == 0) listPanel.Children.Add(new TextBlock { Text = words == "" ? "Nothing found." : $"Nothing matches \"{words}\". Use Browse for a program that isn't running.", Foreground = quietBrush, Margin = new Thickness(10), TextWrapping = TextWrapping.Wrap });
    }

    FrameworkElement RowFor(AppCatalog.Choice choice)
    {
        var listed = alreadyListed.Contains(choice.Path);
        var box = new CheckBox { IsChecked = listed || picked.Contains(choice.Path), IsEnabled = !listed, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
        var text = new StackPanel { Children = { new TextBlock { Text = choice.Name + (listed ? "  (already listed)" : ""), FontSize = 13, FontWeight = choice.HasWindow ? FontWeights.SemiBold : FontWeights.Normal }, new TextBlock { Text = $"{choice.Detail}  ·  {choice.Path}", FontSize = 11.5, Foreground = quietBrush, TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = choice.Path } } };
        var row = new DockPanel { Margin = new Thickness(6, 3, 6, 3), Background = Brushes.Transparent, Cursor = listed ? Cursors.Arrow : Cursors.Hand };
        var icon = new Image { Width = 22, Height = 22, Source = AppIcon.For(choice.Path), Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(box, Dock.Left); DockPanel.SetDock(icon, Dock.Left);
        row.Children.Add(box); row.Children.Add(icon); row.Children.Add(text);
        void Toggle(bool on) { if (on) picked.Add(choice.Path); else picked.Remove(choice.Path); addButton.IsEnabled = picked.Count > 0; addButton.Content = picked.Count > 1 ? $"Add {picked.Count}" : "Add"; }
        box.Click += (sender, args) => Toggle(box.IsChecked == true);
        row.MouseLeftButtonUp += (sender, args) => { if (listed || args.OriginalSource is CheckBox) return; box.IsChecked = box.IsChecked != true; Toggle(box.IsChecked == true); };
        return row;
    }

    void Browse()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Title = "Choose a program to stop", Filter = "Programs (*.exe)|*.exe", InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles) };
        if (dialog.ShowDialog(this) != true || !File.Exists(dialog.FileName)) return;
        if (!choices.Any(choice => choice.Path.Equals(dialog.FileName, StringComparison.OrdinalIgnoreCase))) choices.Insert(0, new AppCatalog.Choice(dialog.FileName, AppCatalog.FriendlyName(dialog.FileName), "Chosen", "Not running now", false));
        picked.Add(dialog.FileName); addButton.IsEnabled = true;
        searchBox.Text = ""; Fill();
    }
}
