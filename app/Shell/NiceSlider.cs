using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
namespace WinSwitch.Shell;

// A flat slider with a round thumb and a filled track, snapping to a fixed number of positions (Mat 17 Nov: less like Windows' own settings)
public sealed class NiceSlider : Grid
{
    readonly Border track, fill;
    readonly Ellipse thumb;
    readonly int positions;
    bool dragging;
    public int Index { get; private set; }
    public bool Dimmed { get => Opacity < 1; set { Opacity = value ? 0.45 : 1; fill.Visibility = value ? Visibility.Hidden : Visibility.Visible; } }
    public event Action<int>? Moving; // while dragging
    public event Action<int>? Chosen; // on release

    public NiceSlider(int positions, int index, Brush? trackBrush = null)
    {
        this.positions = Math.Max(2, positions); Index = Math.Clamp(index, 0, this.positions - 1);
        Height = 26; Background = Brushes.Transparent; Cursor = Cursors.Hand; Margin = new Thickness(9, 0, 9, 0);
        track = new Border { Height = trackBrush == null ? 4 : 6, CornerRadius = new CornerRadius(3), Background = trackBrush ?? new SolidColorBrush(Color.FromRgb(0xE3, 0xE6, 0xEA)), VerticalAlignment = VerticalAlignment.Center };
        fill = new Border { Height = 4, CornerRadius = new CornerRadius(2), Background = new SolidColorBrush(Color.FromRgb(0x2F, 0x7F, 0xD8)), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center, Visibility = trackBrush == null ? Visibility.Visible : Visibility.Collapsed };
        thumb = new Ellipse { Width = 18, Height = 18, Fill = Brushes.White, Stroke = trackBrush == null ? new SolidColorBrush(Color.FromRgb(0x2F, 0x7F, 0xD8)) : new SolidColorBrush(Color.FromRgb(0x3A, 0x9A, 0x6A)), StrokeThickness = trackBrush == null ? 2 : 3, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center, Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 4, ShadowDepth = 1, Opacity = 0.25 } };
        Children.Add(track); Children.Add(fill); Children.Add(thumb);
        SizeChanged += (sender, args) => Place();
        MouseLeftButtonDown += (sender, args) => { dragging = true; CaptureMouse(); MoveTo(args.GetPosition(this).X); args.Handled = true; };
        MouseMove += (sender, args) => { if (dragging) MoveTo(args.GetPosition(this).X); };
        MouseLeftButtonUp += (sender, args) => { if (!dragging) return; dragging = false; ReleaseMouseCapture(); Chosen?.Invoke(Index); args.Handled = true; };
        Focusable = true;
        KeyDown += (sender, args) => { var step = args.Key is Key.Right or Key.Up ? 1 : args.Key is Key.Left or Key.Down ? -1 : 0; if (step == 0) return; SetIndex(Index + step); Moving?.Invoke(Index); Chosen?.Invoke(Index); args.Handled = true; };
    }

    public void SetIndex(int index) { Index = Math.Clamp(index, 0, positions - 1); Place(); }

    void MoveTo(double x)
    {
        var index = (int)Math.Round(Math.Clamp(x / Math.Max(1, ActualWidth), 0, 1) * (positions - 1));
        if (index == Index) return;
        Index = index; Place(); Moving?.Invoke(Index);
    }

    void Place()
    {
        var x = ActualWidth * Index / (positions - 1);
        thumb.Margin = new Thickness(x - 9, 0, 0, 0); fill.Width = Math.Max(0, x);
    }
}
