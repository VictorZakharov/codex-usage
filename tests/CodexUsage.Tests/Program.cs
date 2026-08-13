using System.Runtime.InteropServices;
using CodexUsage.App.Settings;
using CodexUsage.App.UI;
using CodexUsage.Authentication;
using CodexUsage.Formatting;
using CodexUsage.History;
using CodexUsage.Models;
using CodexUsage.Parsing;
using CodexUsage.Services;
using CodexUsage.Tokens;

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
            HistoryForecastUsesRateSinceReset();
            HistoryForecastHandlesSafeAndFlatRates();
            HistoryForecastUsesReportedDuration();
            HistoryForecastLearnsSingleActiveHourAndPausesOffHours();
            HistoryScheduleTreatsUnchangedLongGapsAsFlat();
            HistoryForecastSpreadsRoundedUsageAcrossElapsedWorkdays();
            HistoryForecastFallsBackWhenScheduleCannotExplainWindow();
            HistoryFormUsesReportedWindowLabel();
            HistoryFormShowsLearnedOffHoursAndToggle();
            HistoryStoreCompactsAndReloadsSamples();
            HistoryStoreUpgradesLegacySamplesWithDuration();
            TokenUsageReaderAggregatesPeriodResetAndToday();
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
        Equal(UsageWindowKind.Primary, events[0].Window, "primary restore window");
        Equal(38d, events[0].PreviousAvailablePercent, "primary restore previous value");
        Equal(98d, events[0].AvailablePercent, "primary restore current value");
        Equal(UsageWindowKind.Secondary, events[1].Window, "secondary restore window");
    }

    private static void HistoryForecastUsesRateSinceReset()
    {
        var windowStart = new DateTimeOffset(2026, 8, 13, 12, 0, 0, TimeSpan.Zero);
        var resetAt = windowStart.AddHours(5);
        var samples = new[]
        {
            new UsageHistorySample(
                windowStart.AddMinutes(30), 92, null, resetAt, null, TimeSpan.FromHours(5), null),
            new UsageHistorySample(
                windowStart.AddHours(1), 80, null, resetAt, null, TimeSpan.FromHours(5), null),
            new UsageHistorySample(
                windowStart.AddHours(2), 50, null, resetAt, null, TimeSpan.FromHours(5), null),
        };

        var forecast = UsageHistoryAnalysis.ForecastDepletion(samples, UsageWindowKind.Primary);
        NotNull(forecast, "primary depletion forecast");
        Equal(windowStart, forecast!.WindowStartedAt, "primary forecast window start");
        Equal(25d, forecast.ConsumedPercentPerHour, "primary forecast rate since reset");
        Equal(windowStart.AddHours(4), forecast.DepletesAt, "primary forecast depletion time");
        Equal(true, forecast.ReachesZeroBeforeReset, "primary forecast before reset");
        Equal(TimeSpan.FromHours(1), forecast.TimeBeforeReset, "primary forecast lead time");
        Equal<UsageActivitySchedule?>(null, forecast.ActivitySchedule, "short history uses clock-time fallback");
    }

    private static void HistoryForecastHandlesSafeAndFlatRates()
    {
        var windowStart = new DateTimeOffset(2026, 8, 13, 12, 0, 0, TimeSpan.Zero);
        var resetAt = windowStart.AddHours(5);
        var safe = UsageHistoryAnalysis.ForecastDepletion(
            [new UsageHistorySample(
                windowStart.AddHours(2), 80, null, resetAt, null, TimeSpan.FromHours(5), null)],
            UsageWindowKind.Primary);
        var flat = UsageHistoryAnalysis.ForecastDepletion(
            [new UsageHistorySample(
                windowStart.AddHours(2), 100, null, resetAt, null, TimeSpan.FromHours(5), null)],
            UsageWindowKind.Primary);

        NotNull(safe, "safe depletion forecast");
        Equal(windowStart.AddHours(10), safe!.DepletesAt, "safe forecast depletion time");
        Equal(false, safe.ReachesZeroBeforeReset, "safe forecast after reset");
        Equal<TimeSpan?>(null, safe.TimeBeforeReset, "safe forecast has no lead time");
        Equal<UsageDepletionForecast?>(null, flat, "flat usage has no forecast");
    }

    private static void HistoryForecastUsesReportedDuration()
    {
        var windowStart = new DateTimeOffset(2026, 8, 6, 12, 0, 0, TimeSpan.Zero);
        var resetAt = windowStart.AddDays(7);
        var forecast = UsageHistoryAnalysis.ForecastDepletion(
            [new UsageHistorySample(
                windowStart.AddDays(2), 50, null, resetAt, null, TimeSpan.FromDays(7), null)],
            UsageWindowKind.Primary);

        NotNull(forecast, "reported-duration depletion forecast");
        Equal(TimeSpan.FromDays(7), forecast!.Duration, "forecast duration");
        Equal(windowStart, forecast.WindowStartedAt, "reported-duration forecast window start");
        Equal(windowStart.AddDays(4), forecast.DepletesAt, "reported-duration forecast depletion time");
        Equal(true, forecast.ReachesZeroBeforeReset, "reported-duration forecast before reset");
        Equal(TimeSpan.FromDays(3), forecast.TimeBeforeReset, "reported-duration forecast lead time");
    }

    private static void HistoryForecastLearnsSingleActiveHourAndPausesOffHours()
    {
        var windowStart = new DateTimeOffset(2026, 8, 10, 0, 0, 0, TimeSpan.Zero);
        var resetAt = windowStart.AddDays(7);
        var available = 100d;
        var samples = new List<UsageHistorySample>();
        for (var hour = 0; hour <= 24; hour++)
        {
            if (hour > 0 && windowStart.AddHours(hour - 1).Hour == 9)
            {
                available -= 50;
            }

            samples.Add(new UsageHistorySample(
                windowStart.AddHours(hour),
                available,
                null,
                resetAt,
                null,
                TimeSpan.FromDays(7),
                null));
        }

        var forecast = UsageHistoryAnalysis.ForecastDepletion(
            samples,
            UsageWindowKind.Primary,
            TimeZoneInfo.Utc);

        NotNull(forecast, "single-hour activity forecast");
        NotNull(forecast!.ActivitySchedule, "single-hour activity schedule");
        Equal(1, forecast.ActivitySchedule!.ActiveHours.Count, "single active clock hour count");
        Equal(10, forecast.ActivitySchedule.ActiveHours[0], "single active clock hour");
        Equal(23, forecast.ActivitySchedule.OffHours.Count, "single-hour off clock hour count");
        Equal(50d, forecast.ConsumedPercentPerHour, "single-hour active usage rate");
        Equal(windowStart.AddDays(1).AddHours(11), forecast.DepletesAt, "forecast pauses until next active hour");
        Equal(50d, forecast.ProjectedAvailablePercentAt(windowStart.AddDays(1).AddHours(10)), "quota stays flat off hours");
        Equal(25d, forecast.ProjectedAvailablePercentAt(windowStart.AddDays(1).AddHours(10.5)), "quota falls during active hour");
    }

    private static void HistoryForecastSpreadsRoundedUsageAcrossElapsedWorkdays()
    {
        var windowStart = new DateTimeOffset(2026, 8, 10, 10, 0, 0, TimeSpan.Zero);
        var resetAt = windowStart.AddDays(7);
        var samples = Enumerable.Range(0, (2 * 24) + 1)
            .Select(hour =>
            {
                var recordedAt = windowStart.AddHours(hour);
                var available = hour == 2 * 24 ? 99d : 100d;
                return new UsageHistorySample(
                    recordedAt,
                    available,
                    null,
                    resetAt,
                    null,
                    TimeSpan.FromDays(7),
                    null);
            })
            .ToArray();

        var forecast = UsageHistoryAnalysis.ForecastDepletion(
            samples,
            UsageWindowKind.Primary,
            TimeZoneInfo.Utc);

        NotNull(forecast, "rounded multi-day forecast");
        NotNull(forecast!.ActivitySchedule, "rounded multi-day activity schedule");
        Equal(10, forecast.ActivitySchedule!.ActiveHours.Single(), "rounded drop active clock hour");
        Equal(0.5d, forecast.ConsumedPercentPerHour, "rounded drop spreads over both workdays");

        var fractionalSamples = samples
            .Select(sample => sample.RecordedAt == windowStart.AddDays(2)
                ? sample with { PrimaryAvailablePercent = 99.5d }
                : sample)
            .ToArray();
        var fractionalForecast = UsageHistoryAnalysis.ForecastDepletion(
            fractionalSamples,
            UsageWindowKind.Primary,
            TimeZoneInfo.Utc);
        NotNull(fractionalForecast, "fractional availability forecast");
        Equal(0.25d, fractionalForecast!.ConsumedPercentPerHour, "fractional availability remains precise");
    }

    private static void HistoryScheduleTreatsUnchangedLongGapsAsFlat()
    {
        var dayStart = new DateTimeOffset(2026, 8, 10, 0, 0, 0, TimeSpan.Zero);
        var resetAt = dayStart.AddDays(7);
        var samples = new List<UsageHistorySample>();
        var available = 100d;
        for (var hour = 7; hour <= 22; hour++)
        {
            if (hour == 10)
            {
                available -= 10;
            }

            samples.Add(new UsageHistorySample(
                dayStart.AddHours(hour),
                available,
                null,
                resetAt,
                null,
                TimeSpan.FromDays(7),
                null));
        }

        // No intermediate samples overnight, but equal endpoints prove the quota stayed flat.
        samples.Add(new UsageHistorySample(
            dayStart.AddDays(1).AddHours(7),
            available,
            null,
            resetAt,
            null,
            TimeSpan.FromDays(7),
            null));
        for (var hour = 8; hour <= 22; hour++)
        {
            samples.Add(new UsageHistorySample(
                dayStart.AddDays(1).AddHours(hour),
                available,
                null,
                resetAt,
                null,
                TimeSpan.FromDays(7),
                null));
        }

        var schedule = UsageHistoryAnalysis.InferActivitySchedule(
            samples,
            UsageWindowKind.Primary,
            TimeZoneInfo.Utc);

        NotNull(schedule, "flat overnight gap activity schedule");
        Equal(10, schedule!.ActiveHours.Single(), "flat overnight gap active hour");
        Equal(true, schedule.OffHours.Contains(0), "flat overnight gap marks midnight off");
        Equal(true, schedule.OffHours.Contains(6), "flat overnight gap marks early morning off");
    }

    private static void HistoryForecastFallsBackWhenScheduleCannotExplainWindow()
    {
        var historicalStart = new DateTimeOffset(2026, 8, 10, 0, 0, 0, TimeSpan.Zero);
        var samples = Enumerable.Range(0, 25)
            .Select(hour => new UsageHistorySample(
                historicalStart.AddHours(hour),
                hour >= 10 ? 90d : 100d,
                null,
                historicalStart.AddDays(1),
                null,
                TimeSpan.FromDays(1),
                null))
            .ToList();
        var currentWindowStart = historicalStart.AddDays(2);
        samples.Add(new UsageHistorySample(
            currentWindowStart.AddHours(1),
            90d,
            null,
            currentWindowStart.AddHours(5),
            null,
            TimeSpan.FromHours(5),
            null));

        var forecast = UsageHistoryAnalysis.ForecastDepletion(
            samples,
            UsageWindowKind.Primary,
            TimeZoneInfo.Utc);

        NotNull(forecast, "schedule mismatch wall-clock forecast");
        Equal<UsageActivitySchedule?>(null, forecast!.ActivitySchedule, "schedule mismatch uses fallback");
        Equal(10d, forecast.ConsumedPercentPerHour, "schedule mismatch fallback rate");
    }

    private static void HistoryFormUsesReportedWindowLabel()
    {
        var recordedAt = DateTimeOffset.UtcNow;
        using var form = new HistoryForm(
            new AppSettings { Theme = ThemeMode.Dark },
            (_, _, _, _) => new CodexTokenUsageSummary(0, null, 0, 0));
        form.UpdateHistory(
        [
            new UsageHistorySample(
                recordedAt,
                88,
                null,
                recordedAt.AddDays(7),
                null,
                TimeSpan.FromDays(7),
                null),
        ]);

        var status = form.Controls
            .OfType<Label>()
            .Single(label => label.Text.Contains("Latest:", StringComparison.Ordinal))
            .Text;
        var offHoursToggle = form.Controls
            .OfType<CheckBox>()
            .Single(control => control.Text == "Show off-hour flats");
        form.Location = new Point(-10_000, -10_000);
        form.Show();
        Application.DoEvents();
        Contains("Latest: Weekly 88%", status, "history uses reported window label");
        Equal(false, status.Contains("Secondary", StringComparison.Ordinal), "history hides unavailable window");
        Equal(true, offHoursToggle.Enabled, "off-hour toggle remains readable before schedule learning");
        Equal(false, offHoursToggle.Visible, "off-hour toggle hidden before schedule learning");
        form.Hide();
    }

    private static void HistoryFormShowsLearnedOffHoursAndToggle()
    {
        var windowStart = DateTimeOffset.Now.AddHours(-24);
        var resetAt = windowStart.AddDays(7);
        var available = 100d;
        var samples = new List<UsageHistorySample>();
        for (var hour = 0; hour <= 24; hour++)
        {
            var recordedAt = windowStart.AddHours(hour);
            if (hour > 0 && recordedAt.ToLocalTime().Hour is >= 7 and < 23)
            {
                available -= 4;
            }

            samples.Add(new UsageHistorySample(
                recordedAt,
                available,
                null,
                resetAt,
                null,
                TimeSpan.FromDays(7),
                null));
        }

        using var form = new HistoryForm(
            new AppSettings { Theme = ThemeMode.Dark },
            (_, _, _, _) => new CodexTokenUsageSummary(1_000, 500, 250, 1));
        form.UpdateHistory(samples);
        var chart = form.Controls.OfType<UsageHistoryChart>().Single();
        var toggle = form.Controls
            .OfType<CheckBox>()
            .Single(control => control.Text == "Show off-hour flats");
        var subtitle = form.Controls
            .OfType<Label>()
            .Single(control => control.Text.Contains("assumed off hours", StringComparison.OrdinalIgnoreCase));
        form.Location = new Point(-10_000, -10_000);
        form.Show();
        Application.DoEvents();

        Equal(true, chart.HasLearnedOffHours, "history chart learns off hours");
        Equal(true, chart.HasOffHourSegments, "history chart projects off-hour flats");
        Equal("11 PM–7 AM", chart.OffHoursDescription, "history chart off-hour description");
        Equal(true, toggle.Enabled, "off-hour toggle enabled");
        Equal(true, toggle.Visible, "off-hour toggle shown for learned schedule");
        Equal(true, chart.ShowOffHourSegments, "off-hour flats shown by default");
        Contains("pauses", subtitle.Text, "history subtitle explains off hours");
        var expandedForecastSpan = chart.DisplayedForecastSpan;
        toggle.Checked = false;
        Equal(false, chart.ShowOffHourSegments, "off-hour toggle hides flat segments");
        Equal(
            true,
            chart.DisplayedForecastSpan < expandedForecastSpan,
            "off-hour toggle collapses flat intervals on x axis");
        using var collapsedBitmap = new Bitmap(chart.Width, chart.Height);
        chart.DrawToBitmap(collapsedBitmap, new Rectangle(Point.Empty, collapsedBitmap.Size));
        form.Hide();
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
            Equal(TimeSpan.FromHours(5), reloaded[^1].PrimaryDuration, "history stores primary duration");
            Equal(TimeSpan.FromDays(7), reloaded[^1].SecondaryDuration, "history stores secondary duration");
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

    private static void HistoryStoreUpgradesLegacySamplesWithDuration()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"CodexUsage.Tests.{Guid.NewGuid():N}");
        var filePath = Path.Combine(directory, "history.jsonl");
        try
        {
            Directory.CreateDirectory(directory);
            var recordedAt = DateTimeOffset.UtcNow.AddMinutes(-5);
            var primaryReset = recordedAt.AddDays(7);
            var secondaryReset = recordedAt.AddDays(14);
            var legacyLine = System.Text.Json.JsonSerializer.Serialize(new
            {
                recordedAt,
                primaryAvailablePercent = 90,
                secondaryAvailablePercent = 80,
                primaryResetsAt = primaryReset,
                secondaryResetsAt = secondaryReset,
            });
            File.WriteAllText(filePath, legacyLine + Environment.NewLine);

            var store = new UsageHistoryStore(filePath);
            var upgraded = store.Record(new UsageSnapshot(
                "pro",
                new RateLimitWindow(10, primaryReset, TimeSpan.FromDays(7)),
                new RateLimitWindow(20, secondaryReset, TimeSpan.FromDays(14)),
                null,
                [],
                recordedAt.AddMinutes(1)));

            Equal(2, upgraded.Count, "history adds duration upgrade sample");
            Equal<TimeSpan?>(null, upgraded[0].PrimaryDuration, "legacy duration remains optional");
            Equal(TimeSpan.FromDays(7), upgraded[1].PrimaryDuration, "history upgrades primary duration");
            Equal(TimeSpan.FromDays(14), upgraded[1].SecondaryDuration, "history upgrades secondary duration");
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    private static void TokenUsageReaderAggregatesPeriodResetAndToday()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"CodexUsage.Tests.{Guid.NewGuid():N}");
        try
        {
            var sessions = Path.Combine(directory, "sessions", "2026", "08", "12");
            Directory.CreateDirectory(sessions);
            var firstStart = new DateTimeOffset(2026, 8, 10, 0, 0, 0, TimeSpan.Zero);
            WriteTokenSession(
                Path.Combine(sessions, "rollout-first.jsonl"),
                firstStart,
                [
                    (firstStart.AddHours(10), 100L),
                    (firstStart.AddDays(1).AddHours(10), 250L),
                    (firstStart.AddDays(2).AddHours(10), 400L),
                ]);

            var secondStart = firstStart.AddDays(2).AddHours(9);
            WriteTokenSession(
                Path.Combine(sessions, "rollout-second.jsonl"),
                secondStart,
                [(secondStart.AddHours(1.5), 50L)]);

            var reader = new CodexTokenUsageReader(directory, TimeZoneInfo.Utc);
            var summary = reader.Read(
                firstStart.AddDays(2).AddHours(12),
                firstStart.AddHours(12),
                firstStart.AddDays(1).AddHours(12));

            Equal(350L, summary.SelectedPeriodTokens, "selected-period token total");
            Equal<long?>(200L, summary.SinceResetTokens, "since-reset token total");
            Equal(200L, summary.TodayTokens, "today token total");
            Equal(2, summary.SessionFiles, "token session file count");
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    private static void WriteTokenSession(
        string path,
        DateTimeOffset startedAt,
        IReadOnlyList<(DateTimeOffset Timestamp, long TotalTokens)> tokenEvents)
    {
        var lines = new List<string>
        {
            System.Text.Json.JsonSerializer.Serialize(new
            {
                timestamp = startedAt,
                type = "session_meta",
                payload = new { },
            }),
        };
        lines.AddRange(tokenEvents.Select(item => System.Text.Json.JsonSerializer.Serialize(new
        {
            timestamp = item.Timestamp,
            type = "event_msg",
            payload = new
            {
                type = "token_count",
                info = new
                {
                    total_token_usage = new { total_tokens = item.TotalTokens },
                },
            },
        })));
        File.WriteAllLines(path, lines);
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

    private static void NotNull<T>(T? value, string name)
    {
        _assertions++;
        if (value is null)
        {
            throw new InvalidOperationException($"{name}: expected a value.");
        }
    }
}
