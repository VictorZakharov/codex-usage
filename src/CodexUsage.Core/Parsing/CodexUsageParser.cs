using System.Globalization;
using System.Text.Json;
using CodexUsage.Errors;
using CodexUsage.Models;

namespace CodexUsage.Parsing;

public static class CodexUsageParser
{
    public static UsageSnapshot Parse(
        string json,
        DateTimeOffset fetchedAt,
        string? accountEmail = null)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            var planType = ReadString(root, "plan_type") ?? "unknown";
            RateLimitWindow? primary = null;
            RateLimitWindow? secondary = null;

            if (root.TryGetProperty("rate_limit", out var rateLimit) && rateLimit.ValueKind == JsonValueKind.Object)
            {
                primary = ParseWindow(rateLimit, "primary_window");
                secondary = ParseWindow(rateLimit, "secondary_window");
            }

            var credits = ParseCredits(root);
            var additional = ParseAdditionalLimits(root);

            if (primary is null && secondary is null && additional.Count == 0 && credits is null)
            {
                throw new CodexUsageException(
                    CodexUsageErrorKind.InvalidResponse,
                    "Codex returned a response without recognizable usage data.");
            }

            return new UsageSnapshot(
                planType,
                primary,
                secondary,
                credits,
                additional,
                fetchedAt,
                accountEmail);
        }
        catch (CodexUsageException)
        {
            throw;
        }
        catch (JsonException exception)
        {
            throw new CodexUsageException(
                CodexUsageErrorKind.InvalidResponse,
                "Codex returned malformed usage data.",
                exception);
        }
    }

    private static RateLimitWindow? ParseWindow(JsonElement parent, string propertyName)
    {
        if (!parent.TryGetProperty(propertyName, out var element) || element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var usedPercent = ReadDouble(element, "used_percent");
        if (usedPercent is null)
        {
            return null;
        }

        var resetSeconds = ReadLong(element, "reset_at");
        DateTimeOffset? resetsAt = null;
        if (resetSeconds is > 0)
        {
            try
            {
                resetsAt = DateTimeOffset.FromUnixTimeSeconds(resetSeconds.Value);
            }
            catch (ArgumentOutOfRangeException)
            {
                resetsAt = null;
            }
        }

        var durationSeconds = ReadDouble(element, "limit_window_seconds");
        var duration = durationSeconds is > 0
            ? TimeSpan.FromSeconds(durationSeconds.Value)
            : (TimeSpan?)null;

        return new RateLimitWindow(usedPercent.Value, resetsAt, duration);
    }

    private static CreditsInfo? ParseCredits(JsonElement root)
    {
        if (!root.TryGetProperty("credits", out var element) || element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var hasCredits = ReadBoolean(element, "has_credits") ?? false;
        var unlimited = ReadBoolean(element, "unlimited") ?? false;
        var balance = ReadDecimal(element, "balance");
        return new CreditsInfo(hasCredits, unlimited, balance);
    }

    private static IReadOnlyList<AdditionalRateLimit> ParseAdditionalLimits(JsonElement root)
    {
        var result = new List<AdditionalRateLimit>();
        if (!root.TryGetProperty("additional_rate_limits", out var items) || items.ValueKind != JsonValueKind.Array)
        {
            return result;
        }

        foreach (var item in items.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var name = ReadString(item, "limit_name")
                ?? ReadString(item, "metered_feature")
                ?? "Additional limit";
            if (!item.TryGetProperty("rate_limit", out var rateLimit) || rateLimit.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var primary = ParseWindow(rateLimit, "primary_window");
            var secondary = ParseWindow(rateLimit, "secondary_window");
            if (primary is not null || secondary is not null)
            {
                result.Add(new AdditionalRateLimit(name, primary, secondary));
            }
        }

        return result;
    }

    private static string? ReadString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;
    }

    private static double? ReadDouble(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        if (property.ValueKind == JsonValueKind.Number && property.TryGetDouble(out var number))
        {
            return number;
        }

        return property.ValueKind == JsonValueKind.String
            && double.TryParse(property.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out number)
            ? number
            : null;
    }

    private static long? ReadLong(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        if (property.ValueKind == JsonValueKind.Number && property.TryGetInt64(out var number))
        {
            return number;
        }

        return property.ValueKind == JsonValueKind.String
            && long.TryParse(property.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out number)
            ? number
            : null;
    }

    private static decimal? ReadDecimal(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        if (property.ValueKind == JsonValueKind.Number && property.TryGetDecimal(out var number))
        {
            return number;
        }

        return property.ValueKind == JsonValueKind.String
            && decimal.TryParse(property.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out number)
            ? number
            : null;
    }

    private static bool? ReadBoolean(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        return property.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null,
        };
    }
}
