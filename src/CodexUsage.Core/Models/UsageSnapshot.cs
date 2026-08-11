namespace CodexUsage.Models;

public sealed record RateLimitWindow(
    double UsedPercent,
    DateTimeOffset? ResetsAt,
    TimeSpan? Duration)
{
    public double UsedPercentClamped => Math.Clamp(UsedPercent, 0, 100);

    public double AvailablePercent => 100 - UsedPercentClamped;
}

public sealed record CreditsInfo(
    bool HasCredits,
    bool Unlimited,
    decimal? Balance);

public sealed record AdditionalRateLimit(
    string Name,
    RateLimitWindow? Primary,
    RateLimitWindow? Secondary);

public sealed record UsageSnapshot(
    string PlanType,
    RateLimitWindow? Primary,
    RateLimitWindow? Secondary,
    CreditsInfo? Credits,
    IReadOnlyList<AdditionalRateLimit> AdditionalLimits,
    DateTimeOffset FetchedAt,
    string? AccountEmail = null)
{
    public double LowestAvailablePercent
    {
        get
        {
            var values = new List<double>();
            if (Primary is not null)
            {
                values.Add(Primary.AvailablePercent);
            }

            if (Secondary is not null)
            {
                values.Add(Secondary.AvailablePercent);
            }

            foreach (var limit in AdditionalLimits)
            {
                if (limit.Primary is not null)
                {
                    values.Add(limit.Primary.AvailablePercent);
                }

                if (limit.Secondary is not null)
                {
                    values.Add(limit.Secondary.AvailablePercent);
                }
            }

            return values.Count == 0 ? 100 : values.Min();
        }
    }
}
