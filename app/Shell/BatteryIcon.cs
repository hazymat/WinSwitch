using System.Drawing;
using System.Drawing.Drawing2D;
namespace WinSwitch.Shell;

// The tray icon: a bold upright line-art battery filling the square tray slot (Mat 18 Nov, option D, then B: wider and on whole pixels so it's as crisp as Windows' own).
// Fill = charge (green while charging, red at 20% or less), a bolt beside it while plugged in (breathing while charging, see TrayIcon; still and faint when plugged in but not charging), and one dot under the bolt:
// the profile's colour, amber when a question waits, flashing blue when an update is ready. White on a dark taskbar, black on a light one, transparent behind
public static class BatteryIcon
{
    public static Bitmap Draw(int size, double level, bool pluggedIn, bool charging, string dotColour, bool light, double boltStrength = 1)
    {
        var ink = light ? Color.Black : Color.White;
        var bitmap = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            // Battery on whole pixels: no anti-aliasing, so edges stay sharp at every size
            graphics.Clear(Color.Transparent); graphics.SmoothingMode = SmoothingMode.None; graphics.PixelOffsetMode = PixelOffsetMode.Half;
            var stroke = Math.Max(1, (int)Math.Round(size * 0.075)); var capHeight = Math.Max(2, (int)Math.Round(size * 0.1));
            int left = size < 24 ? 0 : 1, right = (int)Math.Round(size * 0.6) - 1, top = capHeight, bottom = size - 1, radius = Math.Max(1, (int)Math.Round(size / 12.0));
            using var inkBrush = new SolidBrush(ink);
            using (var outer = Rounded(left, top, right - left + 1, bottom - top + 1, radius)) graphics.FillPath(inkBrush, outer);
            using (var inner = Rounded(left + stroke, top + stroke, right - left + 1 - stroke * 2, bottom - top + 1 - stroke * 2, Math.Max(0, radius - stroke))) Clear(graphics, inner, 0);
            var bodyWidth = right - left + 1; var capWidth = Math.Max(4, (int)Math.Round(bodyWidth * 0.46)); capWidth += (bodyWidth - capWidth) % 2;
            graphics.FillRectangle(inkBrush, left + (bodyWidth - capWidth) / 2, 0, capWidth, top + 1);
            var gap = stroke + Math.Max(1, (int)Math.Round(size / 24.0));
            int innerLeft = left + gap, innerTop = top + gap, innerRight = right - gap, innerBottom = bottom - gap;
            if (level > 0)
            {
                var rows = Math.Max(1, (int)Math.Round((innerBottom - innerTop + 1) * Math.Min(1, level)));
                var fillColour = charging ? (light ? Color.FromArgb(40, 150, 60) : Color.FromArgb(160, 225, 160)) : level <= 0.2 ? Color.FromArgb(232, 72, 72) : ink;
                using var fillBrush = new SolidBrush(fillColour); graphics.FillRectangle(fillBrush, innerLeft, innerBottom - rows + 1, innerRight - innerLeft + 1, rows);
            }
            // Bolt and dot in the column to the right: drawn smooth at 8x and scaled down
            var column = right + 1 + Math.Max(1, (int)Math.Round(size / 20.0)); var columnWidth = size - column; var columnMiddle = column + columnWidth / 2f;
            const int Super = 8;
            using var smooth = new Bitmap(size * Super, size * Super, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (var smoothGraphics = Graphics.FromImage(smooth))
            {
                smoothGraphics.SmoothingMode = SmoothingMode.AntiAlias; smoothGraphics.Clear(Color.Transparent);
                if (pluggedIn)
                {
                    float width = columnWidth * 0.95f * Super, height = size * 0.52f * Super, boltX = columnMiddle * Super - width / 2, boltY = size * 0.27f * Super - height / 2;
                    using var bolt = new GraphicsPath();
                    bolt.AddPolygon(new[] { new PointF(boltX + width * .62f, boltY), new PointF(boltX, boltY + height * .56f), new PointF(boltX + width * .45f, boltY + height * .56f), new PointF(boltX + width * .38f, boltY + height), new PointF(boltX + width, boltY + height * .42f), new PointF(boltX + width * .55f, boltY + height * .42f) });
                    using var boltBrush = new SolidBrush(Color.FromArgb((int)Math.Round(255 * Math.Clamp(boltStrength, 0, 1)), ink)); smoothGraphics.FillPath(boltBrush, bolt);
                }
                if (TryColour(dotColour, out var colour)) { var dotRadius = columnWidth / 2f + 0.3f; Dot(smoothGraphics, colour, columnMiddle * Super, (size - dotRadius) * Super, dotRadius * Super, 0); }
            }
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic; graphics.PixelOffsetMode = PixelOffsetMode.HighQuality; graphics.CompositingQuality = CompositingQuality.HighQuality;
            graphics.DrawImage(smooth, new Rectangle(0, 0, size, size));
        }
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
        if (band > 0) using (var clearPen = new Pen(Color.Transparent, band) { LineJoin = LineJoin.Round }) graphics.DrawPath(clearPen, shape); // a 0-wide pen would still clear 1 pixel
        using (var clearBrush = new SolidBrush(Color.Transparent)) graphics.FillPath(clearBrush, shape);
        graphics.CompositingMode = CompositingMode.SourceOver;
    }

    static bool TryColour(string colour, out Color result) { result = Color.Empty; if (colour == "") return false; try { result = ColorTranslator.FromHtml(colour); return true; } catch { return false; } }

    static GraphicsPath Rounded(float x, float y, float width, float height, float radius)
    {
        var path = new GraphicsPath(); var diameter = Math.Min(radius * 2, Math.Min(width, height));
        if (diameter <= 0) { path.AddRectangle(new RectangleF(x, y, width, height)); return path; }
        path.AddArc(x, y, diameter, diameter, 180, 90); path.AddArc(x + width - diameter, y, diameter, diameter, 270, 90);
        path.AddArc(x + width - diameter, y + height - diameter, diameter, diameter, 0, 90); path.AddArc(x, y + height - diameter, diameter, diameter, 90, 90);
        path.CloseFigure(); return path;
    }
}
