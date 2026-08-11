using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace CodexUsage.App.UI;

public static class TrayIconRenderer
{
    private static readonly IReadOnlyDictionary<char, string[]> DigitGlyphs =
        new Dictionary<char, string[]>
        {
            ['0'] = ["111", "101", "101", "101", "111"],
            ['1'] = ["010", "110", "010", "010", "111"],
            ['2'] = ["111", "001", "111", "100", "111"],
            ['3'] = ["111", "001", "111", "001", "111"],
            ['4'] = ["101", "101", "111", "001", "001"],
            ['5'] = ["111", "100", "111", "001", "111"],
            ['6'] = ["111", "100", "111", "101", "111"],
            ['7'] = ["111", "001", "010", "010", "010"],
            ['8'] = ["111", "101", "111", "101", "111"],
            ['9'] = ["111", "101", "111", "001", "111"],
        };

    private static readonly string[] CompactOneGlyph = ["1", "1", "1", "1", "1"];

    public static Icon Create(
        double availablePercent,
        ThemePalette palette,
        bool error,
        bool refreshing,
        int size = 16)
    {
        size = Math.Clamp(size, 16, 64);
        using var bitmap = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.PixelOffsetMode = PixelOffsetMode.Half;
        graphics.Clear(Color.Transparent);

        var availability = Math.Clamp(availablePercent, 0, 100);
        var accent = error ? palette.Danger : palette.AvailabilityColor(availability);
        var scale = size / 16f;
        var ringWidth = 2f * scale;
        var ringInset = 1.5f * scale;
        var ringDiameter = size - (ringInset * 2);
        var arcRectangle = new RectangleF(ringInset, ringInset, ringDiameter, ringDiameter);

        using var centerBrush = new SolidBrush(Color.FromArgb(245, 18, 19, 22));
        graphics.FillEllipse(centerBrush, arcRectangle);

        using var basePen = new Pen(Color.FromArgb(110, 112, 114, 122), ringWidth)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
        };
        graphics.DrawArc(basePen, arcRectangle, -90, 359.5f);

        var sweep = error ? 359.5f : (float)availability * 3.595f;
        using var accentPen = new Pen(accent, ringWidth)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
        };
        if (sweep > 0)
        {
            graphics.DrawArc(accentPen, arcRectangle, -90, sweep);
        }

        if (error)
        {
            using var errorPen = new Pen(Color.White, Math.Max(2f, 1.5f * scale))
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round,
            };
            graphics.DrawLine(errorPen, 5f * scale, 5f * scale, 11f * scale, 11f * scale);
            graphics.DrawLine(errorPen, 11f * scale, 5f * scale, 5f * scale, 11f * scale);
        }
        else
        {
            var label = Math.Round(availability).ToString("0");
            DrawPixelNumber(graphics, label, size);
        }

        if (refreshing)
        {
            var dotSize = Math.Max(3f, 3f * scale);
            var dotX = size - dotSize - (0.5f * scale);
            var dotY = 0.5f * scale;
            using var dotBorderBrush = new SolidBrush(Color.FromArgb(245, 18, 19, 22));
            graphics.FillEllipse(
                dotBorderBrush,
                dotX - scale,
                dotY - scale,
                dotSize + (2 * scale),
                dotSize + (2 * scale));
            using var dotBrush = new SolidBrush(palette.Accent);
            graphics.FillEllipse(dotBrush, dotX, dotY, dotSize, dotSize);
        }

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

    private static void DrawPixelNumber(Graphics graphics, string label, int size)
    {
        var glyphs = label
            .Select((digit, index) => label == "100" && index == 0 ? CompactOneGlyph : DigitGlyphs[digit])
            .ToArray();
        var horizontalCellSize = label.Length == 1 ? 2 : 1;
        const int verticalCellSize = 2;
        const int glyphGap = 1;
        var logicalWidth = glyphs.Sum(glyph => glyph[0].Length * horizontalCellSize)
            + ((glyphs.Length - 1) * glyphGap);
        const int logicalHeight = 5 * verticalCellSize;
        var logicalX = (16 - logicalWidth) / 2;
        var logicalY = (16 - logicalHeight) / 2;
        var scale = size / 16f;

        graphics.SmoothingMode = SmoothingMode.None;
        graphics.PixelOffsetMode = PixelOffsetMode.None;
        using var numberBrush = new SolidBrush(Color.White);

        foreach (var glyph in glyphs)
        {
            for (var row = 0; row < glyph.Length; row++)
            {
                for (var column = 0; column < glyph[row].Length; column++)
                {
                    if (glyph[row][column] != '1')
                    {
                        continue;
                    }

                    var left = ScaleCoordinate(logicalX + (column * horizontalCellSize), scale);
                    var top = ScaleCoordinate(logicalY + (row * verticalCellSize), scale);
                    var right = ScaleCoordinate(logicalX + ((column + 1) * horizontalCellSize), scale);
                    var bottom = ScaleCoordinate(logicalY + ((row + 1) * verticalCellSize), scale);
                    graphics.FillRectangle(numberBrush, left, top, right - left, bottom - top);
                }
            }

            logicalX += (glyph[0].Length * horizontalCellSize) + glyphGap;
        }
    }

    private static int ScaleCoordinate(int coordinate, float scale)
        => (int)Math.Round(coordinate * scale, MidpointRounding.AwayFromZero);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr handle);
}
