using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace CodexUsage.App.UI;

public static class TrayIconRenderer
{
    private const int SupersamplingFactor = 8;

    public static Icon Create(
        double availablePercent,
        ThemePalette palette,
        bool error,
        bool refreshing,
        int size = 16)
    {
        size = Math.Clamp(size, 16, 64);
        using var bitmap = RenderBitmap(availablePercent, palette, error, refreshing, size);
        var handle = bitmap.GetHicon();
        try
        {
            using var temporary = Icon.FromHandle(handle);
            return (Icon)temporary.Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    private static Bitmap RenderBitmap(
        double availablePercent,
        ThemePalette palette,
        bool error,
        bool refreshing,
        int size)
    {
        var renderSize = size * SupersamplingFactor;
        using var canvas = new Bitmap(renderSize, renderSize, PixelFormat.Format32bppPArgb);
        using (var graphics = Graphics.FromImage(canvas))
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.CompositingQuality = CompositingQuality.HighQuality;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            graphics.Clear(Color.Transparent);
            DrawIcon(
                graphics,
                availablePercent,
                palette,
                error,
                refreshing,
                renderSize / 16f);
        }

        var result = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using var resultGraphics = Graphics.FromImage(result);
        resultGraphics.CompositingMode = CompositingMode.SourceCopy;
        resultGraphics.CompositingQuality = CompositingQuality.HighQuality;
        resultGraphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
        resultGraphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
        resultGraphics.DrawImage(
            canvas,
            new Rectangle(0, 0, size, size),
            0,
            0,
            renderSize,
            renderSize,
            GraphicsUnit.Pixel);
        return result;
    }

    private static void DrawIcon(
        Graphics graphics,
        double availablePercent,
        ThemePalette palette,
        bool error,
        bool refreshing,
        float scale)
    {
        var availability = Math.Clamp(availablePercent, 0, 100);
        var accent = error ? palette.Danger : palette.AvailabilityColor(availability);
        var ringWidth = 1.3f * scale;
        var ringInset = 0.78f * scale;
        var ringDiameter = (16f * scale) - (ringInset * 2);
        var ringRectangle = new RectangleF(ringInset, ringInset, ringDiameter, ringDiameter);

        using var centerBrush = new SolidBrush(Color.FromArgb(226, 18, 20, 23));
        graphics.FillEllipse(centerBrush, ringRectangle);

        using var basePen = new Pen(Color.FromArgb(92, 174, 177, 185), ringWidth)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
        };
        graphics.DrawEllipse(basePen, ringRectangle);

        using var accentPen = new Pen(accent, ringWidth)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
        };
        if (error || availability >= 99.95d)
        {
            graphics.DrawEllipse(accentPen, ringRectangle);
        }
        else if (availability > 0)
        {
            graphics.DrawArc(accentPen, ringRectangle, -90, (float)availability * 3.6f);
        }

        if (error)
        {
            using var errorPen = new Pen(Color.FromArgb(248, 255, 255, 255), 1.4f * scale)
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round,
            };
            graphics.DrawLine(errorPen, 5.2f * scale, 5.2f * scale, 10.8f * scale, 10.8f * scale);
            graphics.DrawLine(errorPen, 10.8f * scale, 5.2f * scale, 5.2f * scale, 10.8f * scale);
        }
        else
        {
            DrawNumber(graphics, Math.Round(availability).ToString("0"), scale);
        }

        if (refreshing)
        {
            var dotSize = 2.7f * scale;
            var dotX = 12.4f * scale;
            var dotY = 0.9f * scale;
            using var dotBorderBrush = new SolidBrush(Color.FromArgb(235, 18, 20, 23));
            graphics.FillEllipse(
                dotBorderBrush,
                dotX - (0.7f * scale),
                dotY - (0.7f * scale),
                dotSize + (1.4f * scale),
                dotSize + (1.4f * scale));
            using var dotBrush = new SolidBrush(palette.Accent);
            graphics.FillEllipse(dotBrush, dotX, dotY, dotSize, dotSize);
        }
    }

    private static void DrawNumber(Graphics graphics, string label, float scale)
    {
        using var family = CreateNumberFontFamily();
        using var format = (StringFormat)StringFormat.GenericTypographic.Clone();
        format.FormatFlags |= StringFormatFlags.NoWrap;
        using var path = new GraphicsPath();
        path.AddString(
            label,
            family,
            (int)FontStyle.Regular,
            16f * scale,
            PointF.Empty,
            format);

        var bounds = path.GetBounds();
        var (targetWidth, targetHeight) = label.Length switch
        {
            >= 3 => (10.2f * scale, 7f * scale),
            2 => (8.6f * scale, 7.6f * scale),
            _ => (5f * scale, 8f * scale),
        };
        var pathScale = Math.Min(targetWidth / bounds.Width, targetHeight / bounds.Height);
        var center = 8f * scale;
        var offsetX = center - ((bounds.X + (bounds.Width / 2f)) * pathScale);
        var offsetY = center - ((bounds.Y + (bounds.Height / 2f)) * pathScale) + (0.1f * scale);
        using var transform = new Matrix(pathScale, 0, 0, pathScale, offsetX, offsetY);
        path.Transform(transform);

        using var numberBrush = new SolidBrush(Color.FromArgb(250, 255, 255, 255));
        graphics.FillPath(numberBrush, path);
    }

    private static FontFamily CreateNumberFontFamily()
    {
        foreach (var familyName in new[]
                 {
                     "Bahnschrift SemiBold Condensed",
                     "Bahnschrift Condensed",
                     "Segoe UI Variable Small Semibold",
                     "Segoe UI Semibold",
                 })
        {
            try
            {
                return new FontFamily(familyName);
            }
            catch (ArgumentException)
            {
                // Try the next Windows font in the fallback list.
            }
        }

        return new FontFamily(FontFamily.GenericSansSerif.Name);
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr handle);
}
