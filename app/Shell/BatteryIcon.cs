using System.Drawing;
using System.Drawing.Drawing2D;
namespace WinSwitch.Shell;

// The tray icon: a bold upright line-art battery using the full height (Mat 18 Nov, option D). Fill = charge (green while charging, red at 20% or less),
// a bolt beside it while charging, and one dot beside it: the profile's colour, amber when a question waits, flashing blue when an update is ready (TrayIcon).
// White on a dark taskbar, black on a light one, transparent behind. Drawn on a 32-unit grid at 4x and scaled down
public static class BatteryIcon
{
    public static Bitmap Draw(int size, double level, bool charging, string dotColour, bool light)
    {
        const int Super = 4;
        var big = size * Super; var unit = big / 32f;
        using var canvas = new Bitmap(big, big, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(canvas))
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias; graphics.Clear(Color.Transparent);
            var ink = light ? Color.Black : Color.White;
            var stroke = Math.Max(1, (int)Math.Round(size / 13f)) * Super;
            float left = 4 * unit, top = 4 * unit, right = 19.5f * unit, bottom = 31.5f * unit, middle = (left + right) / 2;
            using var inkBrush = new SolidBrush(ink);
            using (var pen = new Pen(ink, stroke)) using (var body = Rounded(left + stroke / 2f, top + stroke / 2f, right - left - stroke, bottom - top - stroke, 3 * unit)) graphics.DrawPath(pen, body);
            using (var cap = Rounded(middle - 4 * unit, 0.5f * unit, 8 * unit, top - 0.5f * unit + stroke / 2f, 1.2f * unit)) graphics.FillPath(inkBrush, cap);
            var gap = stroke + 1.6f * unit;
            float innerLeft = left + gap, innerTop = top + gap, innerRight = right - gap, innerBottom = bottom - gap;
            if (level > 0)
            {
                var fillColour = charging ? (light ? Color.FromArgb(40, 150, 60) : Color.FromArgb(160, 225, 160)) : level <= 0.2 ? Color.FromArgb(232, 72, 72) : ink;
                var fillTop = innerBottom - (innerBottom - innerTop) * (float)Math.Clamp(level, 0.06, 1);
                using var fillBrush = new SolidBrush(fillColour);
                using var fill = Rounded(innerLeft, fillTop, innerRight - innerLeft, innerBottom - fillTop, unit); graphics.FillPath(fillBrush, fill);
            }
            if (charging)
            {
                float height = 15 * unit, width = 9 * unit, boltX = 26.5f * unit - width / 2, boltY = 9 * unit - height / 2;
                using var bolt = new GraphicsPath();
                bolt.AddPolygon(new[] { new PointF(boltX + width * .62f, boltY), new PointF(boltX, boltY + height * .56f), new PointF(boltX + width * .45f, boltY + height * .56f), new PointF(boltX + width * .38f, boltY + height), new PointF(boltX + width, boltY + height * .42f), new PointF(boltX + width * .55f, boltY + height * .42f) });
                graphics.FillPath(inkBrush, bolt);
            }
            if (TryColour(dotColour, out var colour)) Dot(graphics, colour, 26.5f * unit, 26 * unit, 5.2f * unit, 0);
        }
        var bitmap = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(bitmap)) { graphics.InterpolationMode = InterpolationMode.HighQualityBicubic; graphics.PixelOffsetMode = PixelOffsetMode.HighQuality; graphics.CompositingQuality = CompositingQuality.HighQuality; graphics.DrawImage(canvas, new Rectangle(0, 0, size, size)); }
        return bitmap;
    }

    // A dot with a clear ring cut around it, so it never merges into the battery outline
    static void Dot(Graphics graphics, Color colour, float centreX, float centreY, float radius, float ring)
    {
        using var circle = new GraphicsPath(); circle.AddEllipse(centreX - radius, centreY - radius, radius * 2, radius * 2);
        if (ring > 0) Clear(graphics, circle, ring * 2);
        using var brush = new SolidBrush(colour); graphics.FillPath(brush, circle);
    }

    // Makes a shape and a band around it transparent
    static void Clear(Graphics graphics, GraphicsPath shape, float band)
    {
        graphics.CompositingMode = CompositingMode.SourceCopy;
        using (var clearPen = new Pen(Color.Transparent, band) { LineJoin = LineJoin.Round }) graphics.DrawPath(clearPen, shape);
        using (var clearBrush = new SolidBrush(Color.Transparent)) graphics.FillPath(clearBrush, shape);
        graphics.CompositingMode = CompositingMode.SourceOver;
    }

    static bool TryColour(string colour, out Color result) { result = Color.Empty; if (colour == "") return false; try { result = ColorTranslator.FromHtml(colour); return true; } catch { return false; } }

    static GraphicsPath Rounded(float x, float y, float width, float height, float radius)
    {
        var path = new GraphicsPath(); var diameter = Math.Min(radius * 2, Math.Min(width, height));
        path.AddArc(x, y, diameter, diameter, 180, 90); path.AddArc(x + width - diameter, y, diameter, diameter, 270, 90);
        path.AddArc(x + width - diameter, y + height - diameter, diameter, diameter, 0, 90); path.AddArc(x, y + height - diameter, diameter, diameter, 90, 90);
        path.CloseFigure(); return path;
    }
}
