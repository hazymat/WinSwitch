using System.Drawing;
using System.Drawing.Drawing2D;
namespace WinSwitch.Shell;

// The tray icon: a bold upright line-art battery like Windows' own (Mat 18 Nov). Fill = charge (red at 20% or less), a bolt while charging,
// the profile's colour dot beside it (never over it), an amber dot when a question waits. White on a dark taskbar, black on a light one, transparent behind.
// Drawn on a 32-unit grid at 4x and scaled down, so it stays crisp at every scaling.
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
            var hasDot = TryColour(colour, out var dotColour);
            var stroke = Math.Max(1, (int)Math.Round(size / 13f)) * Super;
            float left = (hasDot ? 2 : 6.5f) * unit, right = (hasDot ? 20.5f : 25.5f) * unit, top = 6 * unit, bottom = 31 * unit, middle = (left + right) / 2;
            using var inkBrush = new SolidBrush(ink);
            using (var pen = new Pen(ink, stroke)) using (var body = Rounded(left + stroke / 2f, top + stroke / 2f, right - left - stroke, bottom - top - stroke, 3 * unit)) graphics.DrawPath(pen, body);
            using (var cap = Rounded(middle - 3.5f * unit, 2 * unit, 7 * unit, top - 2 * unit + stroke / 2f, unit)) graphics.FillPath(inkBrush, cap);
            var gap = stroke + 1.5f * unit;
            float innerLeft = left + gap, innerTop = top + gap, innerRight = right - gap, innerBottom = bottom - gap;
            var fillTop = innerBottom - (innerBottom - innerTop) * (float)Math.Clamp(level, 0, 1);
            if (level > 0)
            {
                using var fillBrush = new SolidBrush(level <= 0.2 && !charging ? Color.FromArgb(232, 72, 72) : ink);
                using var fill = Rounded(innerLeft, fillTop, innerRight - innerLeft, innerBottom - fillTop, unit); graphics.FillPath(fillBrush, fill);
            }
            if (charging)
            {
                float height = 15 * unit, width = 9 * unit, boltX = middle - width / 2, boltY = (top + bottom) / 2 - height / 2;
                using var bolt = new GraphicsPath();
                bolt.AddPolygon(new[] { new PointF(boltX + width * .62f, boltY), new PointF(boltX, boltY + height * .56f), new PointF(boltX + width * .45f, boltY + height * .56f), new PointF(boltX + width * .38f, boltY + height), new PointF(boltX + width, boltY + height * .42f), new PointF(boltX + width * .55f, boltY + height * .42f) });
                // A clear gap around the bolt so it reads over the fill, then the bolt itself
                graphics.CompositingMode = CompositingMode.SourceCopy;
                using (var clearPen = new Pen(Color.Transparent, 2.2f * unit) { LineJoin = LineJoin.Round }) { graphics.DrawPath(clearPen, bolt); using var clearBrush = new SolidBrush(Color.Transparent); graphics.FillPath(clearBrush, bolt); }
                graphics.CompositingMode = CompositingMode.SourceOver;
                graphics.FillPath(inkBrush, bolt);
            }
            if (hasDot) { using var dotBrush = new SolidBrush(dotColour); graphics.FillEllipse(dotBrush, 22 * unit, 21.5f * unit, 10 * unit, 10 * unit); }
            if (question) { using var amber = new SolidBrush(Color.FromArgb(224, 145, 47)); graphics.FillEllipse(amber, 23.5f * unit, 2 * unit, 8 * unit, 8 * unit); }
        }
        var bitmap = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(bitmap)) { graphics.InterpolationMode = InterpolationMode.HighQualityBicubic; graphics.PixelOffsetMode = PixelOffsetMode.HighQuality; graphics.CompositingQuality = CompositingQuality.HighQuality; graphics.DrawImage(canvas, new Rectangle(0, 0, size, size)); }
        return bitmap;
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
