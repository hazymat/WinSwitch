using System.Drawing;
using System.Drawing.Drawing2D;
namespace WinSwitch.Shell;

// The tray icon: a bold line-art battery on its side like Windows' own (Mat 18 Nov, option A). Fill = charge (green while charging, red at 20% or less),
// a white bolt with a clear gap around it while charging, the profile's colour dot on the bottom-right corner with a clear ring (as Windows badges its lock icon),
// an amber dot top right when a question waits. White on a dark taskbar, black on a light one, transparent behind. Drawn on a 32-unit grid at 4x and scaled down
public static class BatteryIcon
{
    public static Bitmap Draw(int size, double level, bool charging, string colour, bool question, bool light)
    {
        const int Super = 4;
        var big = size * Super; var unit = big / 32f;
        using var canvas = new Bitmap(big, big, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(canvas))
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias; graphics.Clear(Color.Transparent);
            var ink = light ? Color.Black : Color.White;
            var stroke = Math.Max(1, (int)Math.Round(size / 13f)) * Super;
            float left = 1 * unit, top = 5.5f * unit, right = 27.5f * unit, bottom = 26.5f * unit;
            using var inkBrush = new SolidBrush(ink);
            using (var pen = new Pen(ink, stroke)) using (var body = Rounded(left + stroke / 2f, top + stroke / 2f, right - left - stroke, bottom - top - stroke, 3 * unit)) graphics.DrawPath(pen, body);
            using (var cap = Rounded(right - stroke / 2f, 11.5f * unit, 30.5f * unit - right + stroke / 2f, 9 * unit, 1.2f * unit)) graphics.FillPath(inkBrush, cap);
            var gap = stroke + 1.6f * unit;
            float innerLeft = left + gap, innerTop = top + gap, innerRight = right - gap, innerBottom = bottom - gap;
            if (level > 0)
            {
                var fillColour = charging ? (light ? Color.FromArgb(40, 150, 60) : Color.FromArgb(160, 225, 160)) : level <= 0.2 ? Color.FromArgb(232, 72, 72) : ink;
                using var fillBrush = new SolidBrush(fillColour);
                using var fill = Rounded(innerLeft, innerTop, (innerRight - innerLeft) * (float)Math.Clamp(level, 0.04, 1), innerBottom - innerTop, unit); graphics.FillPath(fillBrush, fill);
            }
            if (charging)
            {
                float height = 17 * unit, width = 10.5f * unit, boltX = (left + right) / 2 - width / 2, boltY = (top + bottom) / 2 - height / 2;
                using var bolt = new GraphicsPath();
                bolt.AddPolygon(new[] { new PointF(boltX + width * .62f, boltY), new PointF(boltX, boltY + height * .56f), new PointF(boltX + width * .45f, boltY + height * .56f), new PointF(boltX + width * .38f, boltY + height), new PointF(boltX + width, boltY + height * .42f), new PointF(boltX + width * .55f, boltY + height * .42f) });
                Clear(graphics, bolt, 2.4f * unit);
                graphics.FillPath(inkBrush, bolt);
            }
            if (TryColour(colour, out var dotColour)) Dot(graphics, dotColour, 25.5f * unit, 26 * unit, 6 * unit, 1.5f * unit);
            if (question) Dot(graphics, Color.FromArgb(224, 145, 47), 27 * unit, 5 * unit, 4.5f * unit, 1.5f * unit);
        }
        var bitmap = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(bitmap)) { graphics.InterpolationMode = InterpolationMode.HighQualityBicubic; graphics.PixelOffsetMode = PixelOffsetMode.HighQuality; graphics.CompositingQuality = CompositingQuality.HighQuality; graphics.DrawImage(canvas, new Rectangle(0, 0, size, size)); }
        return bitmap;
    }

    // A dot with a clear ring cut around it, so it never merges into the battery outline
    static void Dot(Graphics graphics, Color colour, float centreX, float centreY, float radius, float ring)
    {
        using var circle = new GraphicsPath(); circle.AddEllipse(centreX - radius, centreY - radius, radius * 2, radius * 2);
        Clear(graphics, circle, ring * 2);
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
