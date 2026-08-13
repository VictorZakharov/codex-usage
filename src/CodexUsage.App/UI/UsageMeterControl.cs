using System.Drawing.Drawing2D;
using CodexUsage.Models;

namespace CodexUsage.App.UI;

public sealed class UsageMeterControl : Control
{
    private const float LogicalDpi = 96f;

    private ThemePalette _palette = ThemePalette.Resolve(Settings.ThemeMode.System);
    private string _title = "Usage";
    private string _detail = "Waiting for Codex";
    private double? _availablePercent;

    public UsageMeterControl()
    {
        DoubleBuffered = true;
        Height = 78;
        SetStyle(ControlStyles.ResizeRedraw, true);
    }

    public void SetData(string title, RateLimitWindow? window, string detail, ThemePalette palette)
    {
        _title = title;
        _detail = detail;
        _availablePercent = window?.AvailablePercent;
        _palette = palette;
        Invalidate();
    }

    public void SetLoading(string title, ThemePalette palette)
    {
        _title = title;
        _detail = "Loading usage…";
        _availablePercent = null;
        _palette = palette;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var graphics = e.Graphics;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        var scale = DeviceDpi / LogicalDpi;

        using var background = new SolidBrush(_palette.Card);
        using var path = RoundedRectangle(ClientRectangle, 10f * scale);
        graphics.FillPath(background, path);
        using var border = new Pen(_palette.Border, Math.Max(1f, scale));
        graphics.DrawPath(border, path);

        var innerLeft = 14f * scale;
        using var titleFont = new Font(Font.FontFamily, 9.25f, FontStyle.Bold);
        using var percentFont = new Font(Font.FontFamily, 12.5f, FontStyle.Bold);
        using var detailFont = new Font(Font.FontFamily, 8.25f, FontStyle.Regular);
        using var textBrush = new SolidBrush(_palette.Text);
        using var secondaryBrush = new SolidBrush(_palette.SecondaryText);

        graphics.DrawString(_title, titleFont, textBrush, innerLeft, 10f * scale);

        var percentText = _availablePercent is null ? "—" : $"{_availablePercent:0.#}% available";
        var percentSize = graphics.MeasureString(percentText, percentFont);
        graphics.DrawString(
            percentText,
            percentFont,
            textBrush,
            Width - (14f * scale) - percentSize.Width,
            7f * scale);
        graphics.DrawString(_detail, detailFont, secondaryBrush, innerLeft, 34f * scale);

        var trackRectangle = new RectangleF(
            innerLeft,
            Height - (14f * scale),
            Width - (innerLeft * 2),
            5f * scale);
        using var trackPath = RoundedRectangle(trackRectangle, 3f * scale);
        using var trackBrush = new SolidBrush(_palette.Border);
        graphics.FillPath(trackBrush, trackPath);

        if (_availablePercent is > 0)
        {
            var width = Math.Max(
                5f * scale,
                trackRectangle.Width * (float)(_availablePercent.Value / 100d));
            var fillRectangle = new RectangleF(trackRectangle.X, trackRectangle.Y, width, trackRectangle.Height);
            using var fillPath = RoundedRectangle(fillRectangle, 3f * scale);
            using var fillBrush = new SolidBrush(_palette.AvailabilityColor(_availablePercent.Value));
            graphics.FillPath(fillBrush, fillPath);
        }
    }

    private static GraphicsPath RoundedRectangle(Rectangle rectangle, float radius)
        => RoundedRectangle(new RectangleF(rectangle.X, rectangle.Y, rectangle.Width - 1, rectangle.Height - 1), radius);

    private static GraphicsPath RoundedRectangle(RectangleF rectangle, float radius)
    {
        var diameter = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(rectangle.X, rectangle.Y, diameter, diameter, 180, 90);
        path.AddArc(rectangle.Right - diameter, rectangle.Y, diameter, diameter, 270, 90);
        path.AddArc(rectangle.Right - diameter, rectangle.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(rectangle.X, rectangle.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }
}
