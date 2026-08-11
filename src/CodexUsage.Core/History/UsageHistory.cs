using CodexUsage.Models;

namespace CodexUsage.History;

public enum UsageWindowKind
{
    FiveHour,
    Weekly,
}

public sealed record UsageHistorySample(
    DateTimeOffset RecordedAt,
    double? PrimaryAvailablePercent,
    double? SecondaryAvailablePercent,
    DateTimeOffset? PrimaryResetsAt,
    DateTimeOffset? SecondaryResetsAt)
{
    public static UsageHistorySample FromSnapshot(UsageSnapshot snapshot)
        => new(
            snapshot.FetchedAt,
            snapshot.Primary?.AvailablePercent,
            snapshot.Secondary?.AvailablePercent,
            snapshot.Primary?.ResetsAt,
            snapshot.Secondary?.ResetsAt);
}

public sealed record UsageRestoreEvent(
    DateTimeOffset RecordedAt,
    UsageWindowKind Window,
    double PreviousAvailablePercent,
    double AvailablePercent);

public static class UsageHistoryAnalysis
{
    public const double MinimumRestoreJump = 5d;

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
                UsageWindowKind.FiveHour);
            AddRestoreEvent(
                events,
                ordered[index - 1].SecondaryAvailablePercent,
                ordered[index].SecondaryAvailablePercent,
                ordered[index].RecordedAt,
                UsageWindowKind.Weekly);
        }

        return events;
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
