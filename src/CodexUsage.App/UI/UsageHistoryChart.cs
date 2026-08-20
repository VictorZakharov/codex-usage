using System.ComponentModel;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using CodexUsage.Formatting;
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
    private RectangleF _forecastRectangle;
    private DateTimeOffset? _forecastEnd;
    private HoverPoint? _hoveredPoint;
    private bool _showOffHourSegments = true;

    public UsageHistoryChart()
    {
        DoubleBuffered = true;
        SetStyle(ControlStyles.ResizeRedraw, true);
        Cursor = Cursors.Cross;
        MouseLeave += (_, _) => ClearHover();
    }

    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool ShowOffHourSegments
    {
        get => _showOffHourSegments;
        set
        {
            if (_showOffHourSegments == value)
            {
                return;
            }

            _showOffHourSegments = value;
            Invalidate();
        }
    }

    public bool HasLearnedOffHours
        => _depletionForecasts.Any(forecast => forecast.ActivitySchedule is not null);

    public bool HasOffHourSegments
    {
        get
        {
            var timeline = VisibleInterval();
            var cursor = timeline.HistoryEnd;
            while (cursor < timeline.End)
            {
                var segmentEnd = NextForecastDisplayBoundary(cursor, timeline.End);
                if (!IsForecastTimeDisplayed(cursor))
                {
                    return true;
                }

                cursor = segmentEnd;
            }

            return false;
        }
    }

    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public TimeSpan DisplayedForecastSpan
    {
        get
        {
            var timeline = VisibleInterval();
            return DisplayDuration(timeline.HistoryEnd, timeline.End, timeline);
        }
    }

    public string? OffHoursDescription
    {
        get
        {
            var descriptions = _depletionForecasts
                .Select(forecast => forecast.ActivitySchedule)
                .OfType<UsageActivitySchedule>()
                .Select(FormatOffHours)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            return descriptions.Length == 0
                ? null
                : string.Join(" / ", descriptions);
        }
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
            .Where(forecast => forecast.ProjectionEndsAt > now
                && forecast.ResetsAt > now)
            .OrderBy(forecast => forecast.ProjectionEndsAt)
            .ToArray();
        _forecastEnd = _depletionForecasts.Count == 0
            ? null
            : _depletionForecasts.Max(forecast => forecast.ProjectionEndsAt);
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

        var timeline = VisibleInterval();
        var hoveredPoint = HitTestPoint(eventArgs.Location, timeline);
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

        var legendHeight = OffHoursDescription is null ? 52 : 70;
        _plotRectangle = new RectangleF(54, legendHeight, Width - 76, Height - legendHeight - 44);
        var timeline = VisibleInterval();
        ConfigureForecastRectangle(timeline);
        DrawForecastBackground(graphics);
        DrawLegend(graphics);

        DrawGrid(graphics, timeline);
        var visibleSamples = VisibleSamples(timeline.Start, timeline.HistoryEnd);
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
            timeline);
        DrawSeries(
            graphics,
            visibleSamples,
            sample => sample.SecondaryAvailablePercent,
            secondaryColor,
            timeline);
        DrawRestoreMarkers(graphics, timeline);
        DrawDepletionForecasts(graphics, timeline);
        DrawHoveredPoint(graphics, timeline, primaryColor, secondaryColor);

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
        var latest = _samples.LastOrDefault();
        var primaryLabel = FormatWindowLegendLabel(latest?.PrimaryDuration, "Primary");
        var secondaryLabel = FormatWindowLegendLabel(latest?.SecondaryDuration, "Secondary");

        var x = 18f;
        if (_samples.Any(sample => sample.PrimaryAvailablePercent is not null))
        {
            x = DrawLineLegend(graphics, legendFont, textBrush, x, primaryLabel, primaryColor);
        }

        if (_samples.Any(sample => sample.SecondaryAvailablePercent is not null))
        {
            x = DrawLineLegend(graphics, legendFont, textBrush, x, secondaryLabel, secondaryColor);
        }

        if (_restoreEvents.Count > 0)
        {
            DrawUpwardTriangle(graphics, _palette.Success, new PointF(x + 5, 24), 5f);
            graphics.DrawString("Reset", legendFont, textBrush, x + 16, 15);
            x += 16 + graphics.MeasureString("Reset", legendFont).Width + 20;
        }

        if (_depletionForecasts.Count > 0)
        {
            _ = DrawLineLegend(
                graphics,
                legendFont,
                textBrush,
                x,
                "Projected usage",
                _palette.Danger,
                dashed: true);
        }

        if (OffHoursDescription is { } offHours)
        {
            if (_showOffHourSegments)
            {
                DrawLegendLine(graphics, _palette.Danger, 18, 43, dashed: true);
            }

            graphics.DrawString(
                _showOffHourSegments
                    ? $"Assumed off hours excluded: {offHours}"
                    : $"Assumed off hours collapsed: {offHours}",
                legendFont,
                textBrush,
                _showOffHourSegments ? 38 : 18,
                34);
        }
    }

    private void DrawGrid(Graphics graphics, TimelineInterval timeline)
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

        const int tickCount = 6;
        var nowX = MapX(timeline.HistoryEnd, timeline);
        for (var index = 0; index < tickCount; index++)
        {
            var fraction = index / (double)(tickCount - 1);
            var x = _plotRectangle.Left + ((float)fraction * _plotRectangle.Width);
            if (_forecastEnd is not null && Math.Abs(x - nowX) < 36)
            {
                continue;
            }

            graphics.DrawLine(gridPen, x, _plotRectangle.Top, x, _plotRectangle.Bottom);
            var instant = TimestampAtDisplayFraction(timeline, fraction);
            var label = _forecastEnd is null && index == tickCount - 1
                ? "Now"
                : FormatAxisTime(instant.ToLocalTime(), timeline.End - timeline.Start);
            var size = graphics.MeasureString(label, axisFont);
            var labelX = Math.Clamp(
                x - (size.Width / 2),
                _plotRectangle.Left,
                _plotRectangle.Right - size.Width);
            graphics.DrawString(label, axisFont, labelBrush, labelX, _plotRectangle.Bottom + 8);
        }

        if (_forecastEnd is not null)
        {
            const string label = "Now";
            var size = graphics.MeasureString(label, axisFont);
            var labelX = Math.Clamp(
                nowX - (size.Width / 2),
                _plotRectangle.Left,
                _plotRectangle.Right - size.Width);
            graphics.DrawString(
                label,
                axisFont,
                labelBrush,
                labelX,
                _plotRectangle.Bottom + 8);
        }
    }

    private void ConfigureForecastRectangle(TimelineInterval timeline)
    {
        if (_forecastEnd is null)
        {
            _forecastRectangle = RectangleF.Empty;
            return;
        }

        var forecastLeft = MapX(timeline.HistoryEnd, timeline);
        _forecastRectangle = new RectangleF(
            forecastLeft,
            _plotRectangle.Top,
            Math.Max(0, _plotRectangle.Right - forecastLeft),
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
        TimelineInterval timeline)
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
        using var offHourPen = new Pen(Color.FromArgb(190, _palette.Danger), 2f)
        {
            DashStyle = DashStyle.Dot,
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
        };
        using var endpointBrush = new SolidBrush(_palette.Danger);
        using var labelFont = new Font(Font.FontFamily, 8f, FontStyle.Bold);
        using var hintFont = new Font(Font.FontFamily, 7.5f, FontStyle.Regular);
        using var labelBrush = new SolidBrush(_palette.Danger);
        using var hintBrush = new SolidBrush(_palette.SecondaryText);
        using var labelBackground = new SolidBrush(_palette.Card);
        using var labelFormat = new StringFormat
        {
            FormatFlags = StringFormatFlags.NoWrap,
            Trimming = StringTrimming.EllipsisCharacter,
        };

        for (var index = 0; index < _depletionForecasts.Count; index++)
        {
            var forecast = _depletionForecasts[index];
            DrawForecastSegments(graphics, forecast, timeline, linePen, offHourPen);
            var projectionEnd = forecast.ProjectionEndsAt;
            var projectedAvailable = forecast.ProjectedAvailablePercentAt(projectionEnd);
            var endpoint = new PointF(
                MapX(projectionEnd, timeline),
                MapY(projectedAvailable));
            graphics.FillEllipse(endpointBrush, endpoint.X - 4, endpoint.Y - 4, 8, 8);

            var windowLabel = FormatForecastWindowLabel(forecast.Duration);
            var label = forecast.ReachesZeroBeforeReset
                ? $"{windowLabel} → 0%  {FormatForecastTime(projectionEnd)}"
                : $"{windowLabel} → {projectedAvailable:0.#}% at reset";
            var hint = forecast.ReachesZeroBeforeReset
                ? FormatResetLeadTime(forecast.TimeBeforeReset!.Value)
                : FormatForecastTime(projectionEnd);
            var labelWidth = Math.Min(225, _plotRectangle.Width - 16);
            var labelLeft = Math.Max(_plotRectangle.Left + 8, _plotRectangle.Right - labelWidth - 8);
            var blockRectangle = new RectangleF(
                labelLeft,
                _forecastRectangle.Top + 7 + (index * 39),
                labelWidth,
                36);
            var labelRectangle = new RectangleF(
                blockRectangle.Left,
                blockRectangle.Top + 1,
                blockRectangle.Width,
                17);
            var hintRectangle = new RectangleF(
                blockRectangle.Left,
                blockRectangle.Top + 18,
                blockRectangle.Width,
                16);
            graphics.FillRectangle(labelBackground, blockRectangle);
            graphics.DrawString(label, labelFont, labelBrush, labelRectangle, labelFormat);
            graphics.DrawString(
                hint,
                hintFont,
                hintBrush,
                hintRectangle,
                labelFormat);
        }
    }

    private void DrawForecastSegments(
        Graphics graphics,
        UsageDepletionForecast forecast,
        TimelineInterval timeline,
        Pen activePen,
        Pen offHourPen)
    {
        var cursor = forecast.RecordedAt;
        var projectionEnd = forecast.ProjectionEndsAt;
        while (cursor < projectionEnd)
        {
            var schedule = forecast.ActivitySchedule;
            var isOffHour = schedule is not null && !schedule.IsActive(cursor);
            var segmentEnd = schedule?.NextHourBoundary(cursor) ?? projectionEnd;
            if (segmentEnd > projectionEnd)
            {
                segmentEnd = projectionEnd;
            }

            if (!isOffHour || _showOffHourSegments)
            {
                var startPoint = new PointF(
                    MapX(cursor, timeline),
                    MapY(forecast.ProjectedAvailablePercentAt(cursor)));
                var endPoint = new PointF(
                    MapX(segmentEnd, timeline),
                    MapY(forecast.ProjectedAvailablePercentAt(segmentEnd)));
                graphics.DrawLine(isOffHour ? offHourPen : activePen, startPoint, endPoint);
            }

            cursor = segmentEnd;
        }
    }

    private void DrawSeries(
        Graphics graphics,
        IReadOnlyList<UsageHistorySample> samples,
        Func<UsageHistorySample, double?> selector,
        Color color,
        TimelineInterval timeline)
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
                MapX(sample.RecordedAt, timeline),
                MapY(value.Value)));
        }

        DrawSegment(graphics, pen, pointBrush, segment, showSamplePoints);
    }

    private void DrawRestoreMarkers(
        Graphics graphics,
        TimelineInterval timeline)
    {
        foreach (var restore in _restoreEvents)
        {
            if (restore.RecordedAt < timeline.Start || restore.RecordedAt > timeline.End)
            {
                continue;
            }

            var point = new PointF(
                MapX(restore.RecordedAt, timeline),
                MapY(restore.AvailablePercent));
            DrawUpwardTriangle(graphics, _palette.Success, point, 5.5f);
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

    private TimelineInterval VisibleInterval()
    {
        var now = DateTimeOffset.Now;
        var historyEnd = _samples.Length > 0 && _samples[^1].RecordedAt > now
            ? _samples[^1].RecordedAt
            : now;
        var rangeStart = historyEnd - _range;
        var earliestSample = _samples.FirstOrDefault()?.RecordedAt ?? historyEnd;
        var start = earliestSample > rangeStart ? earliestSample : rangeStart;
        var minimumHistoryStart = historyEnd - TimeSpan.FromDays(1);
        if (start > minimumHistoryStart)
        {
            start = minimumHistoryStart;
        }

        var end = _forecastEnd is { } forecastEnd && forecastEnd > historyEnd
            ? forecastEnd
            : historyEnd;
        return new TimelineInterval(start, historyEnd, end);
    }

    private TimeSpan DisplayDuration(
        DateTimeOffset start,
        DateTimeOffset end,
        TimelineInterval timeline)
    {
        if (end <= start)
        {
            return TimeSpan.Zero;
        }

        var cursor = start;
        var displayedTicks = 0L;
        if (cursor < timeline.HistoryEnd)
        {
            var historySegmentEnd = EarlierOf(end, timeline.HistoryEnd);
            displayedTicks = checked(displayedTicks + (historySegmentEnd - cursor).Ticks);
            cursor = historySegmentEnd;
        }

        if (cursor >= end)
        {
            return TimeSpan.FromTicks(displayedTicks);
        }

        if (_showOffHourSegments)
        {
            return TimeSpan.FromTicks(checked(displayedTicks + (end - cursor).Ticks));
        }

        while (cursor < end)
        {
            var segmentEnd = NextForecastDisplayBoundary(cursor, end);
            if (IsForecastTimeDisplayed(cursor))
            {
                displayedTicks = checked(displayedTicks + (segmentEnd - cursor).Ticks);
            }

            cursor = segmentEnd;
        }

        return TimeSpan.FromTicks(displayedTicks);
    }

    private DateTimeOffset TimestampAtDisplayFraction(
        TimelineInterval timeline,
        double fraction)
    {
        var total = DisplayDuration(timeline.Start, timeline.End, timeline);
        var targetTicks = (long)(total.Ticks * Math.Clamp(fraction, 0d, 1d));
        var historyTicks = (timeline.HistoryEnd - timeline.Start).Ticks;
        if (targetTicks <= historyTicks)
        {
            return timeline.Start.AddTicks(targetTicks);
        }

        var remainingTicks = targetTicks - historyTicks;
        if (_showOffHourSegments)
        {
            return EarlierOf(timeline.HistoryEnd.AddTicks(remainingTicks), timeline.End);
        }

        var cursor = timeline.HistoryEnd;
        while (cursor < timeline.End)
        {
            var segmentEnd = NextForecastDisplayBoundary(cursor, timeline.End);
            if (IsForecastTimeDisplayed(cursor))
            {
                var segmentTicks = (segmentEnd - cursor).Ticks;
                if (remainingTicks <= segmentTicks)
                {
                    return cursor.AddTicks(remainingTicks);
                }

                remainingTicks -= segmentTicks;
            }

            cursor = segmentEnd;
        }

        return timeline.End;
    }

    private bool IsForecastTimeDisplayed(DateTimeOffset timestamp)
    {
        var hasForecast = false;
        foreach (var forecast in _depletionForecasts)
        {
            if (timestamp >= forecast.ProjectionEndsAt)
            {
                continue;
            }

            hasForecast = true;
            if (forecast.ActivitySchedule is null
                || forecast.ActivitySchedule.IsActive(timestamp))
            {
                return true;
            }
        }

        return !hasForecast;
    }

    private DateTimeOffset NextForecastDisplayBoundary(
        DateTimeOffset timestamp,
        DateTimeOffset end)
    {
        var boundary = end;
        foreach (var forecast in _depletionForecasts)
        {
            var projectionEnd = forecast.ProjectionEndsAt;
            if (projectionEnd > timestamp && projectionEnd < boundary)
            {
                boundary = projectionEnd;
            }

            if (forecast.ActivitySchedule is not { } schedule
                || projectionEnd <= timestamp)
            {
                continue;
            }

            var hourBoundary = schedule.NextHourBoundary(timestamp);
            if (hourBoundary < boundary)
            {
                boundary = hourBoundary;
            }
        }

        return boundary > timestamp ? boundary : end;
    }

    private static DateTimeOffset EarlierOf(DateTimeOffset first, DateTimeOffset second)
        => first <= second ? first : second;

    private float MapX(DateTimeOffset timestamp, TimelineInterval timeline)
    {
        var elapsed = DisplayDuration(timeline.Start, timestamp, timeline);
        var total = DisplayDuration(timeline.Start, timeline.End, timeline);
        var fraction = elapsed.TotalSeconds / Math.Max(1d, total.TotalSeconds);
        return _plotRectangle.Left + ((float)Math.Clamp(fraction, 0d, 1d) * _plotRectangle.Width);
    }

    private float MapY(double value)
        => _plotRectangle.Top + ((float)(1d - (Math.Clamp(value, 0, 100) / 100d)) * _plotRectangle.Height);

    private static string FormatAxisTime(DateTimeOffset timestamp, TimeSpan span)
    {
        if (span <= TimeSpan.FromDays(1))
        {
            return timestamp.ToString("h tt");
        }

        return span <= TimeSpan.FromDays(7)
            ? timestamp.ToString("ddd h tt")
            : timestamp.ToString("MMM d");
    }

    private static string FormatWindowLegendLabel(TimeSpan? duration, string fallback)
    {
        const string suffix = " limit";
        var label = UsageText.WindowLabel(duration, fallback + suffix);
        return label.EndsWith(suffix, StringComparison.Ordinal)
            ? label[..^suffix.Length]
            : label;
    }

    private static string FormatForecastWindowLabel(TimeSpan duration)
    {
        if (duration.TotalHours is >= 4.5 and <= 5.5)
        {
            return "5h";
        }

        if (duration.TotalDays is >= 6.5 and <= 7.5)
        {
            return "Week";
        }

        return duration.TotalDays >= 1
            ? $"{Math.Round(duration.TotalDays):0}d"
            : $"{Math.Round(duration.TotalHours):0}h";
    }

    private static string FormatResetLeadTime(TimeSpan leadTime)
    {
        var totalMinutes = Math.Max(1, (int)Math.Floor(leadTime.TotalMinutes));
        var days = totalMinutes / (24 * 60);
        var hours = (totalMinutes / 60) % 24;
        var minutes = totalMinutes % 60;
        if (days > 0)
        {
            return hours > 0
                ? $"{days}d {hours}h before reset"
                : $"{days}d before reset";
        }

        return hours > 0
            ? minutes > 0
                ? $"{hours}h {minutes}m before reset"
                : $"{hours}h before reset"
            : $"{minutes}m before reset";
    }

    private static string FormatOffHours(UsageActivitySchedule schedule)
    {
        var offHours = schedule.OffHours.ToHashSet();
        var starts = Enumerable.Range(0, 24)
            .Where(hour => offHours.Contains(hour) && !offHours.Contains((hour + 23) % 24))
            .ToArray();
        var ranges = starts.Select(start =>
        {
            var end = (start + 1) % 24;
            while (offHours.Contains(end))
            {
                end = (end + 1) % 24;
            }

            return $"{FormatClockHour(start)}–{FormatClockHour(end)}";
        });
        return string.Join(", ", ranges);
    }

    private static string FormatClockHour(int hour)
        => new DateTime(2000, 1, 1, hour, 0, 0).ToString("h tt");

    private static string FormatForecastTime(DateTimeOffset timestamp)
    {
        var local = timestamp.ToLocalTime();
        return local.Date == DateTime.Today
            ? local.ToString("h:mm tt")
            : local.ToString("ddd h:mm tt");
    }

    private HoverPoint? HitTestPoint(Point location, TimelineInterval timeline)
    {
        const float hitRadius = 9f;
        var position = Math.Clamp(
            (location.X - _plotRectangle.Left) / _plotRectangle.Width,
            0f,
            1f);
        var target = TimestampAtDisplayFraction(timeline, position);
        var nearestIndex = FindNearestSampleIndex(target);
        var firstIndex = Math.Max(0, nearestIndex - 512);
        var lastIndex = Math.Min(_samples.Length - 1, nearestIndex + 512);
        HoverPoint? closest = null;
        var closestDistanceSquared = hitRadius * hitRadius;

        for (var index = firstIndex; index <= lastIndex; index++)
        {
            var sample = _samples[index];
            if (sample.RecordedAt < timeline.Start || sample.RecordedAt > timeline.End)
            {
                continue;
            }

            var x = MapX(sample.RecordedAt, timeline);
            if (Math.Abs(x - location.X) > hitRadius)
            {
                continue;
            }

            TestPoint(sample, UsageWindowKind.Primary, sample.PrimaryAvailablePercent, x);
            TestPoint(sample, UsageWindowKind.Secondary, sample.SecondaryAvailablePercent, x);
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
        var windowName = UsageText.WindowLabel(
            point.Sample.GetDuration(point.Window),
            point.Window == UsageWindowKind.Primary ? "Primary limit" : "Secondary limit");
        var restored = _restoreEvents.Any(item =>
            item.RecordedAt == point.Sample.RecordedAt && item.Window == point.Window);
        var suffix = restored ? "\nAvailability restored / reset" : string.Empty;
        return $"{windowName}\n{point.Sample.RecordedAt.ToLocalTime():ddd, MMM d · h:mm tt}"
            + $"\n{point.AvailablePercent:0.#}% available"
            + $"\n{100 - point.AvailablePercent:0.#}% consumed{suffix}";
    }

    private void DrawHoveredPoint(
        Graphics graphics,
        TimelineInterval timeline,
        Color primaryColor,
        Color secondaryColor)
    {
        if (_hoveredPoint is null
            || _hoveredPoint.Sample.RecordedAt < timeline.Start
            || _hoveredPoint.Sample.RecordedAt > timeline.End)
        {
            return;
        }

        var color = _hoveredPoint.Window == UsageWindowKind.Primary ? primaryColor : secondaryColor;
        var point = new PointF(
            MapX(_hoveredPoint.Sample.RecordedAt, timeline),
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

    private sealed record TimelineInterval(
        DateTimeOffset Start,
        DateTimeOffset HistoryEnd,
        DateTimeOffset End);
}
