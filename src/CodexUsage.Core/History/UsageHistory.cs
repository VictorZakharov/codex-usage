using CodexUsage.Models;

namespace CodexUsage.History;

public enum UsageWindowKind
{
    Primary,
    Secondary,
}

public sealed record UsageHistorySample(
    DateTimeOffset RecordedAt,
    double? PrimaryAvailablePercent,
    double? SecondaryAvailablePercent,
    DateTimeOffset? PrimaryResetsAt,
    DateTimeOffset? SecondaryResetsAt,
    TimeSpan? PrimaryDuration = null,
    TimeSpan? SecondaryDuration = null)
{
    public static UsageHistorySample FromSnapshot(UsageSnapshot snapshot)
        => new(
            snapshot.FetchedAt,
            snapshot.Primary?.AvailablePercent,
            snapshot.Secondary?.AvailablePercent,
            snapshot.Primary?.ResetsAt,
            snapshot.Secondary?.ResetsAt,
            snapshot.Primary?.Duration,
            snapshot.Secondary?.Duration);

    public double? GetAvailablePercent(UsageWindowKind window)
        => window switch
        {
            UsageWindowKind.Primary => PrimaryAvailablePercent,
            UsageWindowKind.Secondary => SecondaryAvailablePercent,
            _ => throw new ArgumentOutOfRangeException(nameof(window), window, null),
        };

    public DateTimeOffset? GetResetsAt(UsageWindowKind window)
        => window switch
        {
            UsageWindowKind.Primary => PrimaryResetsAt,
            UsageWindowKind.Secondary => SecondaryResetsAt,
            _ => throw new ArgumentOutOfRangeException(nameof(window), window, null),
        };

    public TimeSpan? GetDuration(UsageWindowKind window)
        => window switch
        {
            UsageWindowKind.Primary => PrimaryDuration,
            UsageWindowKind.Secondary => SecondaryDuration,
            _ => throw new ArgumentOutOfRangeException(nameof(window), window, null),
        };
}

public sealed record UsageRestoreEvent(
    DateTimeOffset RecordedAt,
    UsageWindowKind Window,
    double PreviousAvailablePercent,
    double AvailablePercent);

public sealed class UsageActivitySchedule
{
    private readonly bool[] _activeHours;

    public UsageActivitySchedule(IEnumerable<int> activeHours, TimeZoneInfo timeZone)
    {
        ArgumentNullException.ThrowIfNull(activeHours);
        ArgumentNullException.ThrowIfNull(timeZone);

        _activeHours = new bool[24];
        foreach (var hour in activeHours)
        {
            if (hour is < 0 or > 23)
            {
                throw new ArgumentOutOfRangeException(nameof(activeHours), hour, "Hours must be from 0 through 23.");
            }

            _activeHours[hour] = true;
        }

        TimeZone = timeZone;
        ActiveHours = Enumerable.Range(0, 24).Where(hour => _activeHours[hour]).ToArray();
        OffHours = Enumerable.Range(0, 24).Where(hour => !_activeHours[hour]).ToArray();
    }

    public TimeZoneInfo TimeZone { get; }

    public IReadOnlyList<int> ActiveHours { get; }

    public IReadOnlyList<int> OffHours { get; }

    public bool IsActive(DateTimeOffset timestamp)
        => _activeHours[TimeZoneInfo.ConvertTime(timestamp, TimeZone).Hour];

    public TimeSpan GetActiveDuration(DateTimeOffset start, DateTimeOffset end)
    {
        if (end <= start)
        {
            return TimeSpan.Zero;
        }

        var cursor = start;
        var activeTicks = 0L;
        while (cursor < end)
        {
            var segmentEnd = EarlierOf(NextHourBoundary(cursor), end);
            if (IsActive(cursor))
            {
                activeTicks = checked(activeTicks + (segmentEnd - cursor).Ticks);
            }

            cursor = segmentEnd;
        }

        return TimeSpan.FromTicks(activeTicks);
    }

    public DateTimeOffset AddActiveDuration(DateTimeOffset start, TimeSpan activeDuration)
    {
        if (activeDuration < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(activeDuration));
        }

        if (ActiveHours.Count == 0)
        {
            throw new InvalidOperationException("An activity schedule must contain at least one active hour.");
        }

        var cursor = start;
        var remainingTicks = activeDuration.Ticks;
        while (remainingTicks > 0)
        {
            var segmentEnd = NextHourBoundary(cursor);
            if (IsActive(cursor))
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

        return cursor;
    }

    public DateTimeOffset NextHourBoundary(DateTimeOffset timestamp)
    {
        var local = TimeZoneInfo.ConvertTime(timestamp, TimeZone);
        var ticksIntoHour = local.TimeOfDay.Ticks % TimeSpan.TicksPerHour;
        var ticksUntilBoundary = TimeSpan.TicksPerHour - ticksIntoHour;
        return timestamp.AddTicks(ticksUntilBoundary);
    }

    private static DateTimeOffset EarlierOf(DateTimeOffset first, DateTimeOffset second)
        => first <= second ? first : second;
}

public sealed record UsageDepletionForecast(
    UsageWindowKind Window,
    DateTimeOffset WindowStartedAt,
    DateTimeOffset RecordedAt,
    DateTimeOffset ResetsAt,
    TimeSpan Duration,
    double AvailablePercent,
    double ConsumedPercentPerHour,
    DateTimeOffset DepletesAt,
    UsageActivitySchedule? ActivitySchedule = null)
{
    public bool ReachesZeroBeforeReset => DepletesAt <= ResetsAt;

    public TimeSpan? TimeBeforeReset
        => ReachesZeroBeforeReset ? ResetsAt - DepletesAt : null;

    public double ProjectedAvailablePercentAt(DateTimeOffset timestamp)
    {
        if (timestamp <= RecordedAt)
        {
            return AvailablePercent;
        }

        var elapsed = ActivitySchedule?.GetActiveDuration(RecordedAt, timestamp)
            ?? timestamp - RecordedAt;
        return Math.Clamp(
            AvailablePercent - (ConsumedPercentPerHour * elapsed.TotalHours),
            0d,
            100d);
    }
}

public static class UsageHistoryAnalysis
{
    public const double MinimumRestoreJump = 5d;

    private const double MinimumConsumedPercent = 0.01d;
    private const double MinimumObservedHoursPerClockHour = 0.25d;

    private static readonly TimeSpan MaximumScheduleSampleGap = TimeSpan.FromMinutes(75);

    public static IReadOnlyList<UsageRestoreEvent> DetectRestoreEvents(
        IEnumerable<UsageHistorySample> samples)
    {
        var ordered = samples.OrderBy(sample => sample.RecordedAt).ToArray();
        var events = new List<UsageRestoreEvent>();

        for (var index = 1; index < ordered.Length; index++)
        {
            AddRestoreEvent(
                events,
                ordered[index - 1].PrimaryAvailablePercent,
                ordered[index].PrimaryAvailablePercent,
                ordered[index].RecordedAt,
                UsageWindowKind.Primary);
            AddRestoreEvent(
                events,
                ordered[index - 1].SecondaryAvailablePercent,
                ordered[index].SecondaryAvailablePercent,
                ordered[index].RecordedAt,
                UsageWindowKind.Secondary);
        }

        return events;
    }

    public static UsageDepletionForecast? ForecastDepletion(
        IEnumerable<UsageHistorySample> samples,
        UsageWindowKind window,
        TimeZoneInfo? timeZone = null)
    {
        var ordered = samples.OrderBy(sample => sample.RecordedAt).ToArray();
        var latest = ordered.LastOrDefault();
        if (latest is null)
        {
            return null;
        }

        var available = latest.GetAvailablePercent(window);
        var resetsAt = latest.GetResetsAt(window);
        var duration = latest.GetDuration(window);
        if (available is null || resetsAt is null || duration is null || duration <= TimeSpan.Zero)
        {
            return null;
        }

        // Each window begins fully available; its next reset minus its reported duration is the last reset.
        var windowStartedAt = resetsAt.Value - duration.Value;
        var elapsed = latest.RecordedAt - windowStartedAt;
        var availablePercent = Math.Clamp(available.Value, 0d, 100d);
        var consumedPercent = 100d - availablePercent;
        if (elapsed <= TimeSpan.Zero || resetsAt <= latest.RecordedAt || consumedPercent < MinimumConsumedPercent)
        {
            return null;
        }

        var activitySchedule = InferActivitySchedule(
            ordered,
            window,
            timeZone ?? TimeZoneInfo.Local);
        var effectiveElapsed = activitySchedule?.GetActiveDuration(windowStartedAt, latest.RecordedAt)
            ?? elapsed;
        if (activitySchedule is not null && effectiveElapsed <= TimeSpan.Zero)
        {
            // The learned schedule has no active time in this window yet, so it cannot
            // explain consumption already reported by the service. Use the wall-clock
            // fallback instead of suppressing the forecast entirely.
            activitySchedule = null;
            effectiveElapsed = elapsed;
        }

        if (effectiveElapsed <= TimeSpan.Zero)
        {
            return null;
        }

        var ratePerHour = consumedPercent / effectiveElapsed.TotalHours;
        var remainingActiveHours = availablePercent / ratePerHour;
        if (!double.IsFinite(remainingActiveHours))
        {
            return null;
        }

        DateTimeOffset depletesAt;
        if (activitySchedule is null)
        {
            if (remainingActiveHours > (DateTimeOffset.MaxValue - latest.RecordedAt).TotalHours)
            {
                return null;
            }

            depletesAt = latest.RecordedAt.AddHours(remainingActiveHours);
        }
        else
        {
            var activeTimeBeforeReset = activitySchedule
                .GetActiveDuration(latest.RecordedAt, resetsAt.Value)
                .TotalHours;
            depletesAt = remainingActiveHours <= activeTimeBeforeReset
                ? activitySchedule.AddActiveDuration(
                    latest.RecordedAt,
                    TimeSpan.FromHours(remainingActiveHours))
                : resetsAt.Value.AddTicks(1);
        }

        return new UsageDepletionForecast(
            window,
            windowStartedAt,
            latest.RecordedAt,
            resetsAt.Value,
            duration.Value,
            availablePercent,
            ratePerHour,
            depletesAt,
            activitySchedule);
    }

    public static UsageActivitySchedule? InferActivitySchedule(
        IEnumerable<UsageHistorySample> samples,
        UsageWindowKind window,
        TimeZoneInfo? timeZone = null)
    {
        var zone = timeZone ?? TimeZoneInfo.Local;
        var ordered = samples.OrderBy(sample => sample.RecordedAt).ToArray();
        var observedHours = new double[24];
        var activeHours = new bool[24];

        for (var index = 1; index < ordered.Length; index++)
        {
            var previous = ordered[index - 1];
            var current = ordered[index];
            var previousAvailable = previous.GetAvailablePercent(window);
            var currentAvailable = current.GetAvailablePercent(window);
            var gap = current.RecordedAt - previous.RecordedAt;
            if (previousAvailable is null
                || currentAvailable is null
                || gap <= TimeSpan.Zero)
            {
                continue;
            }

            var consumed = previousAvailable.Value - currentAvailable.Value >= MinimumConsumedPercent;
            var unchanged = Math.Abs(previousAvailable.Value - currentAvailable.Value)
                < MinimumConsumedPercent;
            if (gap <= MaximumScheduleSampleGap || unchanged)
            {
                // Equal endpoints establish a flat interval even when the app did not
                // sample inside it (for example, overnight). A long interval containing
                // consumption is ambiguous, so do not classify its intervening hours.
                AccumulateByLocalHour(
                    previous.RecordedAt,
                    current.RecordedAt,
                    zone,
                    (hour, duration) =>
                    {
                        observedHours[hour] += duration.TotalHours;
                    });
            }

            if (consumed)
            {
                activeHours[TimeZoneInfo.ConvertTime(current.RecordedAt, zone).Hour] = true;
            }
        }

        if (observedHours.Any(hours => hours < MinimumObservedHoursPerClockHour)
            || !activeHours.Any(active => active)
            || activeHours.All(active => active))
        {
            return null;
        }

        return new UsageActivitySchedule(
            Enumerable.Range(0, 24).Where(hour => activeHours[hour]),
            zone);
    }

    private static void AddRestoreEvent(
        ICollection<UsageRestoreEvent> events,
        double? previous,
        double? current,
        DateTimeOffset recordedAt,
        UsageWindowKind window)
    {
        if (previous is null || current is null || current.Value - previous.Value < MinimumRestoreJump)
        {
            return;
        }

        events.Add(new UsageRestoreEvent(
            recordedAt,
            window,
            Math.Clamp(previous.Value, 0, 100),
            Math.Clamp(current.Value, 0, 100)));
    }

    private static void AccumulateByLocalHour(
        DateTimeOffset start,
        DateTimeOffset end,
        TimeZoneInfo timeZone,
        Action<int, TimeSpan> accumulator)
    {
        var cursor = start;
        while (cursor < end)
        {
            var local = TimeZoneInfo.ConvertTime(cursor, timeZone);
            var ticksIntoHour = local.TimeOfDay.Ticks % TimeSpan.TicksPerHour;
            var segmentEnd = cursor.AddTicks(TimeSpan.TicksPerHour - ticksIntoHour);
            if (segmentEnd > end)
            {
                segmentEnd = end;
            }

            accumulator(local.Hour, segmentEnd - cursor);
            cursor = segmentEnd;
        }
    }
}
