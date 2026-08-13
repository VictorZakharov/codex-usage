using System.Globalization;
using CodexUsage.Models;

namespace CodexUsage.Formatting;

public static class UsageText
{
    public static string WindowLabel(RateLimitWindow window, string fallback)
        => WindowLabel(window.Duration, fallback);

    public static string WindowLabel(TimeSpan? duration, string fallback)
    {
        if (duration is null)
        {
            return fallback;
        }

        var value = duration.Value;
        if (value.TotalHours is >= 4.5 and <= 5.5)
        {
            return "5-hour limit";
        }

        if (value.TotalDays is >= 6.5 and <= 7.5)
        {
            return "Weekly limit";
        }

        if (value.TotalDays >= 1)
        {
            return $"{Math.Round(value.TotalDays):0}-day limit";
        }

        return $"{Math.Round(value.TotalHours):0}-hour limit";
    }

    public static string ResetDescription(DateTimeOffset? resetsAt, DateTimeOffset now)
    {
        if (resetsAt is null)
        {
            return "Reset time unavailable";
        }

        var remaining = resetsAt.Value - now;
        if (remaining <= TimeSpan.Zero)
        {
            return "Resetting now";
        }

        var relative = remaining.TotalDays >= 1
            ? $"{(int)remaining.TotalDays}d {remaining.Hours}h"
            : remaining.TotalHours >= 1
                ? $"{(int)remaining.TotalHours}h {remaining.Minutes}m"
                : $"{Math.Max(1, remaining.Minutes)}m";
        var local = resetsAt.Value.ToLocalTime().ToString("ddd h:mm tt", CultureInfo.CurrentCulture);
        return $"Resets in {relative} · {local}";
    }

    public static string PlanLabel(string planType)
    {
        if (string.IsNullOrWhiteSpace(planType) || planType.Equals("unknown", StringComparison.OrdinalIgnoreCase))
        {
            return "Codex";
        }

        return planType.Replace('_', ' ') switch
        {
            var text when text.Length == 0 => "Codex",
            var text => CultureInfo.CurrentCulture.TextInfo.ToTitleCase(text),
        };
    }
}
