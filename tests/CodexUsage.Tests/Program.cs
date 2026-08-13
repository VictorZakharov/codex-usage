using System.Runtime.InteropServices;
using CodexUsage.App.Settings;
using CodexUsage.App.UI;
using CodexUsage.Authentication;
using CodexUsage.Formatting;
using CodexUsage.History;
using CodexUsage.Models;
using CodexUsage.Parsing;
using CodexUsage.Services;

namespace CodexUsage.Tests;

internal static class Program
{
    private const uint WmDpiChanged = 0x02E0;
    private static int _assertions;

    [STAThread]
    public static async Task<int> Main(string[] args)
    {
        try
        {
            ApplicationConfiguration.Initialize();
            ParserReadsStandardUsage();
            ParserHandlesStringValuesAndAdditionalLimits();
            CredentialParserReadsSnakeAndCamelCase();
            FormattingProducesUsefulLabels();
            HistoryDetectsRestoredQuota();
            HistoryStoreCompactsAndReloadsSamples();
            PopupLayoutSurvivesDpiChange();

            if (args.Contains("--live", StringComparer.OrdinalIgnoreCase))
            {
                await RunLiveCheckAsync();
            }

            Console.WriteLine($"PASS: {_assertions} assertions");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"FAIL: {exception.Message}");
            return 1;
        }
    }

    private static void PopupLayoutSurvivesDpiChange()
    {
        using var form = new PopupForm(new AppSettings { Theme = ThemeMode.Dark });
        _ = form.Handle;

        var originalDpi = form.DeviceDpi;
        var changedDpi = originalDpi == 144 ? 192 : 144;

        ChangeDpi(form, changedDpi);
        form.UpdateState(null, refreshing: false, error: null);
        AssertPopupLayout(form, changedDpi, "after DPI change");

        ChangeDpi(form, originalDpi);
        form.UpdateState(null, refreshing: false, error: null);
        AssertPopupLayout(form, originalDpi, "after DPI restore");
    }

    private static void ChangeDpi(Form form, int newDpi)
    {
        var oldDpi = form.DeviceDpi;
        var bounds = form.Bounds;
        var suggestedBounds = new NativeRectangle(
            bounds.Left,
            bounds.Top,
            bounds.Left + Scale(bounds.Width, newDpi, oldDpi),
            bounds.Top + Scale(bounds.Height, newDpi, oldDpi));
        var packedDpi = new IntPtr(newDpi | (newDpi << 16));

        SendMessage(form.Handle, WmDpiChanged, packedDpi, ref suggestedBounds);
    }

    private static void AssertPopupLayout(PopupForm form, int expectedDpi, string state)
    {
        Equal(expectedDpi, form.DeviceDpi, $"popup device DPI {state}");
        Equal(
            new Size(form.LogicalToDeviceUnits(382), form.LogicalToDeviceUnits(333)),
            form.ClientSize,
            $"popup client size {state}");

        var primaryMeter = form.Controls.OfType<UsageMeterControl>().First();
        Equal(
            new Rectangle(
                form.LogicalToDeviceUnits(18),
                form.LogicalToDeviceUnits(77),
                form.LogicalToDeviceUnits(346),
                form.LogicalToDeviceUnits(78)),
            primaryMeter.Bounds,
            $"popup meter bounds {state}");
    }

    private static int Scale(int value, int newDpi, int oldDpi)
        => (int)Math.Round(value * (double)newDpi / oldDpi);

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(
        IntPtr windowHandle,
        uint message,
        IntPtr wParam,
        ref NativeRectangle lParam);

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct NativeRectangle(int Left, int Top, int Right, int Bottom);

    private static void ParserReadsStandardUsage()
    {
        const string json = """
            {
              "plan_type": "pro",
              "rate_limit": {
                "primary_window": {
                  "used_percent": 42,
                  "reset_at": 1893456000,
                  "limit_window_seconds": 18000
                },
                "secondary_window": {
                  "used_percent": 12.5,
                  "reset_at": 1894060800,
                  "limit_window_seconds": 604800
                }
              },
              "credits": { "has_credits": true, "unlimited": false, "balance": 17.25 }
            }
            """;

        var snapshot = CodexUsageParser.Parse(json, DateTimeOffset.UnixEpoch, "person@example.com");
        Equal("pro", snapshot.PlanType, "plan");
        Equal(42d, snapshot.Primary?.UsedPercent, "primary percent");
        Equal(TimeSpan.FromHours(5), snapshot.Primary?.Duration, "primary duration");
        Equal(12.5d, snapshot.Secondary?.UsedPercent, "secondary percent");
        Equal(17.25m, snapshot.Credits?.Balance, "credit balance");
        Equal("person@example.com", snapshot.AccountEmail, "email");
        Equal(58d, snapshot.LowestAvailablePercent, "lowest availability");

        var empty = new CodexUsage.Models.UsageSnapshot(
            "unknown",
            null,
            null,
            null,
            [],
            DateTimeOffset.UnixEpoch);
        Equal(100d, empty.LowestAvailablePercent, "default availability");
    }

    private static void ParserHandlesStringValuesAndAdditionalLimits()
    {
        const string json = """
            {
              "plan_type": "business",
              "rate_limit": {
                "primary_window": {
                  "used_percent": "103.5",
                  "reset_at": "1893456000",
                  "limit_window_seconds": "18000"
                }
              },
              "credits": { "balance": "5.50" },
              "additional_rate_limits": [
                {
                  "limit_name": "Spark",
                  "rate_limit": {
                    "primary_window": {
                      "used_percent": 9,
                      "reset_at": 1893456000,
                      "limit_window_seconds": 18000
                    }
                  }
                },
                "ignored"
              ]
            }
            """;

        var snapshot = CodexUsageParser.Parse(json, DateTimeOffset.UnixEpoch);
        Equal(103.5d, snapshot.Primary?.UsedPercent, "raw overage");
        Equal(100d, snapshot.Primary?.UsedPercentClamped, "clamped usage");
        Equal(0d, snapshot.Primary?.AvailablePercent, "available after overage");
        Equal(5.50m, snapshot.Credits?.Balance, "string credits");
        Equal(1, snapshot.AdditionalLimits.Count, "additional count");
        Equal("Spark", snapshot.AdditionalLimits[0].Name, "additional name");
    }

    private static void CredentialParserReadsSnakeAndCamelCase()
    {
        const string snake = """
            {
              "tokens": {
                "access_token": "access",
                "refresh_token": "refresh",
                "id_token": "id",
                "account_id": "account"
              },
              "last_refresh": "2026-01-02T03:04:05Z"
            }
            """;
        const string camel = """
            {
              "tokens": {
                "accessToken": "access2",
                "refreshToken": "refresh2",
                "accountId": "account2"
              }
            }
            """;

        var first = CodexCredentialParser.Parse(snake, "auth.json");
        var second = CodexCredentialParser.Parse(camel, "auth.json");
        Equal("access", first.AccessToken, "snake access");
        Equal("account", first.AccountId, "snake account");
        Equal(2026, first.LastRefresh?.Year, "last refresh year");
        Equal("access2", second.AccessToken, "camel access");
        Equal("account2", second.AccountId, "camel account");
    }

    private static void FormattingProducesUsefulLabels()
    {
        var now = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        var window = new CodexUsage.Models.RateLimitWindow(10, now.AddHours(2).AddMinutes(5), TimeSpan.FromHours(5));
        Equal("5-hour limit", UsageText.WindowLabel(window, "Session"), "window label");
        Contains("2h 5m", UsageText.ResetDescription(window.ResetsAt, now), "reset relative");
        Equal("Free Workspace", UsageText.PlanLabel("free_workspace"), "plan label");
    }

    private static void HistoryDetectsRestoredQuota()
    {
        var now = new DateTimeOffset(2026, 8, 10, 12, 0, 0, TimeSpan.Zero);
        var samples = new[]
        {
            new UsageHistorySample(now, 40, 60, null, null),
            new UsageHistorySample(now.AddMinutes(5), 38, 59, null, null),
            new UsageHistorySample(now.AddMinutes(10), 98, 72, null, null),
        };

        var events = UsageHistoryAnalysis.DetectRestoreEvents(samples);
        Equal(2, events.Count, "restore event count");
        Equal(UsageWindowKind.FiveHour, events[0].Window, "primary restore window");
        Equal(38d, events[0].PreviousAvailablePercent, "primary restore previous value");
        Equal(98d, events[0].AvailablePercent, "primary restore current value");
        Equal(UsageWindowKind.Weekly, events[1].Window, "weekly restore window");
    }

    private static void HistoryStoreCompactsAndReloadsSamples()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"CodexUsage.Tests.{Guid.NewGuid():N}");
        var filePath = Path.Combine(directory, "history.jsonl");
        try
        {
            var now = DateTimeOffset.UtcNow.AddMinutes(-5);
            var store = new UsageHistoryStore(filePath);
            store.Record(CreateSnapshot(now, 10, 20));
            store.Record(CreateSnapshot(now.AddMinutes(1), 10, 20));
            var recorded = store.Record(CreateSnapshot(now.AddMinutes(2), 15, 20));

            Equal(2, recorded.Count, "history compacts flat samples");
            File.AppendAllText(filePath, "{damaged line" + Environment.NewLine);
            var reloaded = new UsageHistoryStore(filePath).Load();
            Equal(2, reloaded.Count, "history reload skips damaged lines");
            Equal(85d, reloaded[^1].PrimaryAvailablePercent, "history stores availability");
        }
        finally
        {
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
            }

            var temporaryPath = filePath + ".tmp";
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }

            if (Directory.Exists(directory))
            {
                Directory.Delete(directory);
            }
        }
    }

    private static UsageSnapshot CreateSnapshot(
        DateTimeOffset fetchedAt,
        double primaryUsed,
        double secondaryUsed)
        => new(
            "pro",
            new RateLimitWindow(
                primaryUsed,
                new DateTimeOffset(2030, 1, 1, 5, 0, 0, TimeSpan.Zero),
                TimeSpan.FromHours(5)),
            new RateLimitWindow(
                secondaryUsed,
                new DateTimeOffset(2030, 1, 8, 0, 0, 0, TimeSpan.Zero),
                TimeSpan.FromDays(7)),
            null,
            [],
            fetchedAt);

    private static async Task RunLiveCheckAsync()
    {
        using var service = new CodexUsageService();
        var snapshot = await service.FetchAsync();
        var primary = snapshot.Primary is null ? "n/a" : $"{snapshot.Primary.AvailablePercent:0.#}% available";
        var secondary = snapshot.Secondary is null ? "n/a" : $"{snapshot.Secondary.AvailablePercent:0.#}% available";
        Console.WriteLine($"LIVE: plan={UsageText.PlanLabel(snapshot.PlanType)}, session={primary}, weekly={secondary}");
    }

    private static void Equal<T>(T expected, T actual, string name)
    {
        _assertions++;
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"{name}: expected '{expected}', got '{actual}'.");
        }
    }

    private static void Contains(string expected, string actual, string name)
    {
        _assertions++;
        if (!actual.Contains(expected, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"{name}: expected '{actual}' to contain '{expected}'.");
        }
    }
}
