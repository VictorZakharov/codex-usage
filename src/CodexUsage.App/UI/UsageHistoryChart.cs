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
    private IReadOnlyList<UsageDepletionForecast> _depletionForecasts = [];
    private ThemePalette _palette = ThemePalette.Resolve(Settings.ThemeMode.System);
    private TimeSpan _range = TimeSpan.FromDays(7);
    private RectangleF _plotRectangle;
    private RectangleF _historyRectangle;
    private RectangleF _forecastRectangle;
    private DateTimeOffset? _forecastEnd;
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
        var now = DateTimeOffset.Now;
        _depletionForecasts = Enum.GetValues<UsageWindowKind>()
            .Select(window => UsageHistoryAnalysis.ForecastDepletion(_samples, window))
            .OfType<UsageDepletionForecast>()
            .Where(forecast => forecast.ReachesZeroBeforeReset
                && forecast.DepletesAt > now
                && forecast.ResetsAt > now)
            .OrderBy(forecast => forecast.DepletesAt)
            .ToArray();
        _forecastEnd = _depletionForecasts.Count == 0
            ? null
            : _depletionForecasts.Max(forecast => forecast.DepletesAt);
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
        if (!_historyRectangle.Contains(eventArgs.Location) || _samples.Length == 0)
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
        ConfigurePlotRectangles();
        DrawForecastBackground(graphics);
        DrawLegend(graphics);

        var (start, end) = VisibleInterval();
        DrawGrid(graphics, start, end);
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
        DrawDepletionForecasts(graphics, start, end);
        DrawHoveredPoint(graphics, start, end, primaryColor, secondaryColor);

        if (visibleSamples.Count == 1 && _depletionForecasts.Count == 0)
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

        var x = DrawLineLegend(graphics, legendFont, textBrush, 18, "5-hour", primaryColor);
        x = DrawLineLegend(graphics, legendFont, textBrush, x, "Weekly", secondaryColor);
        DrawUpwardTriangle(graphics, _palette.Success, new PointF(x + 5, 24), 5f);
        graphics.DrawString("Reset", legendFont, textBrush, x + 16, 15);
        x += 16 + graphics.MeasureString("Reset", legendFont).Width + 20;
        if (_depletionForecasts.Count > 0)
        {
            DrawLineLegend(
                graphics,
                legendFont,
                textBrush,
                x,
                "Projected to 0%",
                _palette.Danger,
                dashed: true);
        }
    }

    private void DrawGrid(Graphics graphics, DateTimeOffset start, DateTimeOffset end)
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

        var tickCount = _forecastEnd is null ? 5 : 4;
        for (var index = 0; index < tickCount; index++)
        {
            var fraction = index / (double)(tickCount - 1);
            var x = _historyRectangle.Left + ((float)fraction * _historyRectangle.Width);
            var instant = start + TimeSpan.FromTicks((long)((end - start).Ticks * fraction));
            var label = _forecastEnd is not null && index == tickCount - 1
                ? "Now"
                : FormatAxisTime(instant.ToLocalTime());
            var size = graphics.MeasureString(label, axisFont);
            var labelX = Math.Clamp(
                x - (size.Width / 2),
                _historyRectangle.Left,
                _historyRectangle.Right - size.Width);
            graphics.DrawString(label, axisFont, labelBrush, labelX, _plotRectangle.Bottom + 8);
        }

        if (_forecastEnd is not null)
        {
            var label = FormatForecastAxisTime(_forecastEnd.Value);
            var size = graphics.MeasureString(label, axisFont);
            graphics.DrawString(
                label,
                axisFont,
                labelBrush,
                _forecastRectangle.Right - size.Width,
                _plotRectangle.Bottom + 8);
        }
    }

    private void ConfigurePlotRectangles()
    {
        if (_forecastEnd is null)
        {
            _historyRectangle = _plotRectangle;
            _forecastRectangle = RectangleF.Empty;
            return;
        }

        var forecastWidth = Math.Clamp(_plotRectangle.Width * 0.24f, 128f, 210f);
        _historyRectangle = new RectangleF(
            _plotRectangle.Left,
            _plotRectangle.Top,
            _plotRectangle.Width - forecastWidth,
            _plotRectangle.Height);
        _forecastRectangle = new RectangleF(
            _historyRectangle.Right,
            _plotRectangle.Top,
            forecastWidth,
            _plotRectangle.Height);
    }

    private void DrawForecastBackground(Graphics graphics)
    {
        if (_forecastEnd is null)
        {
            return;
        }

        using var backgroundBrush = new SolidBrush(Color.FromArgb(
            _palette.IsDark ? 18 : 10,
            _palette.Danger));
        graphics.FillRectangle(backgroundBrush, _forecastRectangle);
        using var dividerPen = new Pen(Color.FromArgb(150, _palette.MutedText))
        {
            DashStyle = DashStyle.Dot,
        };
        graphics.DrawLine(
            dividerPen,
            _forecastRectangle.Left,
            _forecastRectangle.Top,
            _forecastRectangle.Left,
            _forecastRectangle.Bottom);
    }

    private void DrawDepletionForecasts(
        Graphics graphics,
        DateTimeOffset historyStart,
        DateTimeOffset historyEnd)
    {
        if (_forecastEnd is null)
        {
            return;
        }

        using var linePen = new Pen(_palette.Danger, 2.25f)
        {
            DashStyle = DashStyle.Dash,
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
        };
        using var endpointBrush = new SolidBrush(_palette.Danger);
        using var labelFont = new Font(Font.FontFamily, 8f, FontStyle.Bold);
        using var labelBrush = new SolidBrush(_palette.Danger);
        using var labelBackground = new SolidBrush(_palette.Card);
        using var labelFormat = new StringFormat
        {
            FormatFlags = StringFormatFlags.NoWrap,
            Trimming = StringTrimming.EllipsisCharacter,
        };

        for (var index = 0; index < _depletionForecasts.Count; index++)
        {
            var forecast = _depletionForecasts[index];
            var projectedAtHistoryEnd = Math.Clamp(
                100d - (forecast.ConsumedPercentPerHour
                    * (historyEnd - forecast.WindowStartedAt).TotalHours),
                0d,
                100d);
            var actualPoint = new PointF(
                MapX(forecast.RecordedAt, historyStart, historyEnd),
                MapY(forecast.AvailablePercent));
            var currentPoint = new PointF(
                _forecastRectangle.Left,
                MapY(projectedAtHistoryEnd));
            var endpoint = new PointF(
                MapForecastX(forecast.DepletesAt, historyEnd),
                MapY(0));
            graphics.DrawLines(linePen, [actualPoint, currentPoint, endpoint]);
            graphics.FillEllipse(endpointBrush, endpoint.X - 4, endpoint.Y - 4, 8, 8);

            var windowLabel = forecast.Window == UsageWindowKind.FiveHour ? "5h" : "Week";
            var label = $"{windowLabel} → 0%  {FormatForecastTime(forecast.DepletesAt)}";
            var labelRectangle = new RectangleF(
                _forecastRectangle.Left + 8,
                _forecastRectangle.Top + 8 + (index * 19),
                _forecastRectangle.Width - 16,
                18);
            graphics.FillRectangle(labelBackground, labelRectangle);
            graphics.DrawString(label, labelFont, labelBrush, labelRectangle, labelFormat);
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
        var showSamplePoints = samples.Count <= _historyRectangle.Width / 6f;

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
        return _historyRectangle.Left + ((float)Math.Clamp(fraction, 0d, 1d) * _historyRectangle.Width);
    }

    private float MapForecastX(DateTimeOffset timestamp, DateTimeOffset start)
    {
        var fraction = (timestamp - start).TotalSeconds
            / Math.Max(1d, (_forecastEnd!.Value - start).TotalSeconds);
        return _forecastRectangle.Left
            + ((float)Math.Clamp(fraction, 0d, 1d) * _forecastRectangle.Width);
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

    private static string FormatForecastAxisTime(DateTimeOffset timestamp)
    {
        var local = timestamp.ToLocalTime();
        return local.Date == DateTime.Today
            ? local.ToString("h:mm tt")
            : local.ToString("ddd h tt");
    }

    private static string FormatForecastTime(DateTimeOffset timestamp)
    {
        var local = timestamp.ToLocalTime();
        return local.Date == DateTime.Today
            ? local.ToString("h:mm tt")
            : local.ToString("ddd h:mm tt");
    }

    private HoverPoint? HitTestPoint(Point location, DateTimeOffset start, DateTimeOffset end)
    {
        const float hitRadius = 9f;
        var position = Math.Clamp(
            (location.X - _historyRectangle.Left) / _historyRectangle.Width,
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

    private static float DrawLineLegend(
        Graphics graphics,
        Font font,
        Brush textBrush,
        float x,
        string text,
        Color color,
        bool dashed = false)
    {
        DrawLegendLine(graphics, color, x, 24, dashed);
        graphics.DrawString(text, font, textBrush, x + 20, 15);
        return x + 20 + graphics.MeasureString(text, font).Width + 20;
    }

    private static void DrawLegendLine(Graphics graphics, Color color, float x, float y, bool dashed)
    {
        using var pen = new Pen(color, 2.25f)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
            DashStyle = dashed ? DashStyle.Dash : DashStyle.Solid,
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
