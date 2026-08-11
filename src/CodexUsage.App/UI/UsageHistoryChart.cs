using System.Drawing.Drawing2D;
using System.Drawing.Text;
using CodexUsage.History;

namespace CodexUsage.App.UI;

public sealed class UsageHistoryChart : Control
{
    private readonly ToolTip _toolTip = new()
    {
        InitialDelay = 250,
        ReshowDelay = 100,
        AutoPopDelay = 12_000,
    };

    private UsageHistorySample[] _samples = [];
    private IReadOnlyList<UsageRestoreEvent> _restoreEvents = [];
    private ThemePalette _palette = ThemePalette.Resolve(Settings.ThemeMode.System);
    private TimeSpan _range = TimeSpan.FromDays(7);
    private RectangleF _plotRectangle;
    private HoverPoint? _hoveredPoint;

    public UsageHistoryChart()
    {
        DoubleBuffered = true;
        SetStyle(ControlStyles.ResizeRedraw, true);
        Cursor = Cursors.Cross;
        MouseLeave += (_, _) => ClearHover();
    }

    public void SetData(
        IReadOnlyList<UsageHistorySample> samples,
        TimeSpan range,
        ThemePalette palette)
    {
        _samples = samples.OrderBy(sample => sample.RecordedAt).ToArray();
        _restoreEvents = UsageHistoryAnalysis.DetectRestoreEvents(_samples);
        _range = range;
        _palette = palette;
        BackColor = palette.Card;
        ClearHover();
        Invalidate();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _toolTip.Dispose();
        }

        base.Dispose(disposing);
    }

    protected override void OnMouseMove(MouseEventArgs eventArgs)
    {
        base.OnMouseMove(eventArgs);
        if (!_plotRectangle.Contains(eventArgs.Location) || _samples.Length == 0)
        {
            ClearHover();
            return;
        }

        var (start, end) = VisibleInterval();
        var hoveredPoint = HitTestPoint(eventArgs.Location, start, end);
        if (hoveredPoint is null)
        {
            ClearHover();
            return;
        }

        if (_hoveredPoint?.Sample.RecordedAt == hoveredPoint.Sample.RecordedAt
            && _hoveredPoint.Window == hoveredPoint.Window)
        {
            return;
        }

        _hoveredPoint = hoveredPoint;
        _toolTip.Hide(this);
        _toolTip.Show(
            BuildToolTip(hoveredPoint),
            this,
            Point.Round(new PointF(hoveredPoint.Location.X + 10, hoveredPoint.Location.Y + 10)),
            12_000);
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs eventArgs)
    {
        base.OnPaint(eventArgs);
        var graphics = eventArgs.Graphics;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        graphics.Clear(_palette.Card);

        using var borderPen = new Pen(_palette.Border);
        graphics.DrawRectangle(borderPen, 0, 0, Math.Max(0, Width - 1), Math.Max(0, Height - 1));

        if (Width < 260 || Height < 180)
        {
            return;
        }

        _plotRectangle = new RectangleF(54, 52, Width - 76, Height - 96);
        DrawLegend(graphics);
        DrawGrid(graphics);

        var (start, end) = VisibleInterval();
        var visibleSamples = VisibleSamples(start, end);
        if (visibleSamples.Count == 0)
        {
            DrawEmptyState(graphics, "History starts after the next successful refresh.");
            return;
        }

        var primaryColor = _palette.Accent;
        var secondaryColor = _palette.IsDark
            ? Color.FromArgb(185, 151, 255)
            : Color.FromArgb(115, 78, 185);

        DrawSeries(
            graphics,
            visibleSamples,
            sample => sample.PrimaryAvailablePercent,
            primaryColor,
            start,
            end);
        DrawSeries(
            graphics,
            visibleSamples,
            sample => sample.SecondaryAvailablePercent,
            secondaryColor,
            start,
            end);
        DrawRestoreMarkers(graphics, start, end, primaryColor, secondaryColor);
        DrawHoveredPoint(graphics, start, end, primaryColor, secondaryColor);

        if (visibleSamples.Count == 1)
        {
            DrawEmptyState(graphics, "Collecting more samples…");
        }
    }

    private void DrawLegend(Graphics graphics)
    {
        using var legendFont = new Font(Font.FontFamily, 8.5f, FontStyle.Regular);
        using var textBrush = new SolidBrush(_palette.SecondaryText);
        var primaryColor = _palette.Accent;
        var secondaryColor = _palette.IsDark
            ? Color.FromArgb(185, 151, 255)
            : Color.FromArgb(115, 78, 185);

        DrawLegendLine(graphics, primaryColor, 18, 24);
        graphics.DrawString("5-hour available", legendFont, textBrush, 38, 15);
        DrawLegendLine(graphics, secondaryColor, 166, 24);
        graphics.DrawString("Weekly available", legendFont, textBrush, 186, 15);
        DrawUpwardTriangle(graphics, _palette.Success, new PointF(327, 24), 5f);
        graphics.DrawString("Restored / reset", legendFont, textBrush, 339, 15);
    }

    private void DrawGrid(Graphics graphics)
    {
        using var axisFont = new Font(Font.FontFamily, 8f, FontStyle.Regular);
        using var labelBrush = new SolidBrush(_palette.MutedText);
        using var gridPen = new Pen(Color.FromArgb(_palette.IsDark ? 72 : 52, _palette.Border));

        foreach (var value in new[] { 100, 75, 50, 25, 0 })
        {
            var y = MapY(value);
            graphics.DrawLine(gridPen, _plotRectangle.Left, y, _plotRectangle.Right, y);
            var label = $"{value}%";
            var size = graphics.MeasureString(label, axisFont);
            graphics.DrawString(label, axisFont, labelBrush, _plotRectangle.Left - size.Width - 8, y - (size.Height / 2));
        }

        var (start, end) = VisibleInterval();
        for (var index = 0; index <= 4; index++)
        {
            var fraction = index / 4d;
            var x = _plotRectangle.Left + ((float)fraction * _plotRectangle.Width);
            var instant = start + TimeSpan.FromTicks((long)((end - start).Ticks * fraction));
            var label = FormatAxisTime(instant.ToLocalTime());
            var size = graphics.MeasureString(label, axisFont);
            var labelX = Math.Clamp(x - (size.Width / 2), _plotRectangle.Left, _plotRectangle.Right - size.Width);
            graphics.DrawString(label, axisFont, labelBrush, labelX, _plotRectangle.Bottom + 8);
        }
    }

    private void DrawSeries(
        Graphics graphics,
        IReadOnlyList<UsageHistorySample> samples,
        Func<UsageHistorySample, double?> selector,
        Color color,
        DateTimeOffset start,
        DateTimeOffset end)
    {
        using var pen = new Pen(color, 2.25f)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
            LineJoin = LineJoin.Round,
        };
        using var pointBrush = new SolidBrush(color);
        var segment = new List<PointF>();
        var showSamplePoints = samples.Count <= _plotRectangle.Width / 6f;

        foreach (var sample in samples)
        {
            var value = selector(sample);
            if (value is null)
            {
                DrawSegment(graphics, pen, pointBrush, segment, showSamplePoints);
                segment.Clear();
                continue;
            }

            segment.Add(new PointF(
                MapX(sample.RecordedAt, start, end),
                MapY(value.Value)));
        }

        DrawSegment(graphics, pen, pointBrush, segment, showSamplePoints);
    }

    private void DrawRestoreMarkers(
        Graphics graphics,
        DateTimeOffset start,
        DateTimeOffset end,
        Color primaryColor,
        Color secondaryColor)
    {
        foreach (var restore in _restoreEvents)
        {
            if (restore.RecordedAt < start || restore.RecordedAt > end)
            {
                continue;
            }

            var color = restore.Window == UsageWindowKind.FiveHour ? primaryColor : secondaryColor;
            var point = new PointF(
                MapX(restore.RecordedAt, start, end),
                MapY(restore.AvailablePercent));
            DrawUpwardTriangle(graphics, color, point, 5.5f);
        }
    }

    private void DrawEmptyState(Graphics graphics, string message)
    {
        using var font = new Font(Font.FontFamily, 9f, FontStyle.Regular);
        using var brush = new SolidBrush(_palette.MutedText);
        using var format = new StringFormat
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center,
        };
        graphics.DrawString(message, font, brush, _plotRectangle, format);
    }

    private IReadOnlyList<UsageHistorySample> VisibleSamples(DateTimeOffset start, DateTimeOffset end)
    {
        var visible = _samples
            .Where(sample => sample.RecordedAt >= start && sample.RecordedAt <= end)
            .ToList();
        var immediatelyBefore = _samples.LastOrDefault(sample => sample.RecordedAt < start);
        if (immediatelyBefore is not null)
        {
            visible.Insert(0, immediatelyBefore with { RecordedAt = start });
        }

        return visible;
    }

    private (DateTimeOffset Start, DateTimeOffset End) VisibleInterval()
    {
        var now = DateTimeOffset.Now;
        var end = _samples.Length > 0 && _samples[^1].RecordedAt > now ? _samples[^1].RecordedAt : now;
        return (end - _range, end);
    }

    private float MapX(DateTimeOffset timestamp, DateTimeOffset start, DateTimeOffset end)
    {
        var fraction = (timestamp - start).TotalSeconds / Math.Max(1d, (end - start).TotalSeconds);
        return _plotRectangle.Left + ((float)Math.Clamp(fraction, 0d, 1d) * _plotRectangle.Width);
    }

    private float MapY(double value)
        => _plotRectangle.Top + ((float)(1d - (Math.Clamp(value, 0, 100) / 100d)) * _plotRectangle.Height);

    private string FormatAxisTime(DateTimeOffset timestamp)
    {
        if (_range <= TimeSpan.FromDays(1))
        {
            return timestamp.ToString("h tt");
        }

        return _range <= TimeSpan.FromDays(7)
            ? timestamp.ToString("ddd")
            : timestamp.ToString("MMM d");
    }

    private HoverPoint? HitTestPoint(Point location, DateTimeOffset start, DateTimeOffset end)
    {
        const float hitRadius = 9f;
        var position = Math.Clamp(
            (location.X - _plotRectangle.Left) / _plotRectangle.Width,
            0f,
            1f);
        var target = start + TimeSpan.FromTicks((long)((end - start).Ticks * position));
        var nearestIndex = FindNearestSampleIndex(target);
        var firstIndex = Math.Max(0, nearestIndex - 512);
        var lastIndex = Math.Min(_samples.Length - 1, nearestIndex + 512);
        HoverPoint? closest = null;
        var closestDistanceSquared = hitRadius * hitRadius;

        for (var index = firstIndex; index <= lastIndex; index++)
        {
            var sample = _samples[index];
            if (sample.RecordedAt < start || sample.RecordedAt > end)
            {
                continue;
            }

            var x = MapX(sample.RecordedAt, start, end);
            if (Math.Abs(x - location.X) > hitRadius)
            {
                continue;
            }

            TestPoint(sample, UsageWindowKind.FiveHour, sample.PrimaryAvailablePercent, x);
            TestPoint(sample, UsageWindowKind.Weekly, sample.SecondaryAvailablePercent, x);
        }

        return closest;

        void TestPoint(
            UsageHistorySample sample,
            UsageWindowKind window,
            double? availablePercent,
            float x)
        {
            if (availablePercent is null)
            {
                return;
            }

            var point = new PointF(x, MapY(availablePercent.Value));
            var deltaX = point.X - location.X;
            var deltaY = point.Y - location.Y;
            var distanceSquared = (deltaX * deltaX) + (deltaY * deltaY);
            if (distanceSquared > closestDistanceSquared)
            {
                return;
            }

            closestDistanceSquared = distanceSquared;
            closest = new HoverPoint(sample, window, availablePercent.Value, point);
        }
    }

    private int FindNearestSampleIndex(DateTimeOffset target)
    {
        var low = 0;
        var high = _samples.Length - 1;
        while (low <= high)
        {
            var middle = low + ((high - low) / 2);
            if (_samples[middle].RecordedAt < target)
            {
                low = middle + 1;
            }
            else
            {
                high = middle - 1;
            }
        }

        if (low <= 0)
        {
            return 0;
        }

        if (low >= _samples.Length)
        {
            return _samples.Length - 1;
        }

        return target - _samples[low - 1].RecordedAt <= _samples[low].RecordedAt - target
            ? low - 1
            : low;
    }

    private string BuildToolTip(HoverPoint point)
    {
        var windowName = point.Window == UsageWindowKind.FiveHour ? "5-hour limit" : "Weekly limit";
        var restored = _restoreEvents.Any(item =>
            item.RecordedAt == point.Sample.RecordedAt && item.Window == point.Window);
        var suffix = restored ? "\nAvailability restored / reset" : string.Empty;
        return $"{windowName}\n{point.Sample.RecordedAt.ToLocalTime():ddd, MMM d · h:mm tt}"
            + $"\n{point.AvailablePercent:0.#}% available"
            + $"\n{100 - point.AvailablePercent:0.#}% consumed{suffix}";
    }

    private void DrawHoveredPoint(
        Graphics graphics,
        DateTimeOffset start,
        DateTimeOffset end,
        Color primaryColor,
        Color secondaryColor)
    {
        if (_hoveredPoint is null
            || _hoveredPoint.Sample.RecordedAt < start
            || _hoveredPoint.Sample.RecordedAt > end)
        {
            return;
        }

        var color = _hoveredPoint.Window == UsageWindowKind.FiveHour ? primaryColor : secondaryColor;
        var point = new PointF(
            MapX(_hoveredPoint.Sample.RecordedAt, start, end),
            MapY(_hoveredPoint.AvailablePercent));
        using var haloBrush = new SolidBrush(_palette.Card);
        using var pointBrush = new SolidBrush(color);
        using var outlinePen = new Pen(Color.FromArgb(220, _palette.Text), 1f);
        graphics.FillEllipse(haloBrush, point.X - 5, point.Y - 5, 10, 10);
        graphics.FillEllipse(pointBrush, point.X - 3.5f, point.Y - 3.5f, 7, 7);
        graphics.DrawEllipse(outlinePen, point.X - 3.5f, point.Y - 3.5f, 7, 7);
    }

    private void ClearHover()
    {
        if (_hoveredPoint is null)
        {
            return;
        }

        _hoveredPoint = null;
        _toolTip.Hide(this);
        Invalidate();
    }

    private static void DrawSegment(
        Graphics graphics,
        Pen pen,
        Brush pointBrush,
        IReadOnlyList<PointF> points,
        bool showSamplePoints)
    {
        if (points.Count >= 2)
        {
            graphics.DrawLines(pen, points.ToArray());
        }

        if (showSamplePoints)
        {
            foreach (var point in points)
            {
                graphics.FillEllipse(pointBrush, point.X - 1.6f, point.Y - 1.6f, 3.2f, 3.2f);
            }
        }

        if (points.Count > 0)
        {
            var point = points[^1];
            graphics.FillEllipse(pointBrush, point.X - 3, point.Y - 3, 6, 6);
        }
    }

    private static void DrawLegendLine(Graphics graphics, Color color, float x, float y)
    {
        using var pen = new Pen(color, 2.25f)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
        };
        graphics.DrawLine(pen, x, y, x + 13, y);
    }

    private static void DrawUpwardTriangle(Graphics graphics, Color color, PointF center, float radius)
    {
        var points = new[]
        {
            new PointF(center.X, center.Y - radius),
            new PointF(center.X - radius, center.Y + radius),
            new PointF(center.X + radius, center.Y + radius),
        };
        using var brush = new SolidBrush(color);
        using var border = new Pen(Color.FromArgb(210, 255, 255, 255), 1f);
        graphics.FillPolygon(brush, points);
        graphics.DrawPolygon(border, points);
    }

    private sealed record HoverPoint(
        UsageHistorySample Sample,
        UsageWindowKind Window,
        double AvailablePercent,
        PointF Location);
}
