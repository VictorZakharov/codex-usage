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

public sealed record UsageDepletionForecast(
    UsageWindowKind Window,
    DateTimeOffset WindowStartedAt,
    DateTimeOffset RecordedAt,
    DateTimeOffset ResetsAt,
    TimeSpan Duration,
    double AvailablePercent,
    double ConsumedPercentPerHour,
    DateTimeOffset DepletesAt)
{
    public bool ReachesZeroBeforeReset => DepletesAt <= ResetsAt;

    public TimeSpan? TimeBeforeReset
        => ReachesZeroBeforeReset ? ResetsAt - DepletesAt : null;
}

public static class UsageHistoryAnalysis
{
    public const double MinimumRestoreJump = 5d;

    private const double MinimumConsumedPercent = 0.01d;

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
        UsageWindowKind window)
    {
        var latest = samples.OrderBy(sample => sample.RecordedAt).LastOrDefault();
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

        var ratePerHour = consumedPercent / elapsed.TotalHours;
        var hoursToDepletion = 100d / ratePerHour;
        if (!double.IsFinite(hoursToDepletion)
            || hoursToDepletion > (DateTimeOffset.MaxValue - windowStartedAt).TotalHours)
        {
            return null;
        }

        return new UsageDepletionForecast(
            window,
            windowStartedAt,
            latest.RecordedAt,
            resetsAt.Value,
            duration.Value,
            availablePercent,
            ratePerHour,
            windowStartedAt.AddHours(hoursToDepletion));
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
}
