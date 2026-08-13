using System.Drawing.Imaging;
using System.Globalization;
using CodexUsage.App.Settings;
using CodexUsage.History;
using CodexUsage.Models;

namespace CodexUsage.App.UI;

internal static class PreviewRenderer
{
    public static bool TryRender(string[] args)
    {
        if (args.Length != 2)
        {
            return false;
        }

        var outputPath = Path.GetFullPath(args[1]);
        var directory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        if (TryReadIconPercent(args[0], out var availablePercent))
        {
            var renderPng = Path.GetExtension(outputPath).Equals(".png", StringComparison.OrdinalIgnoreCase);
            using var icon = TrayIconRenderer.Create(
                availablePercent,
                ThemePalette.Resolve(ThemeMode.Dark),
                error: false,
                refreshing: false,
                size: renderPng ? 16 : 64);
            if (renderPng)
            {
                using var iconBitmap = icon.ToBitmap();
                iconBitmap.Save(outputPath, ImageFormat.Png);
            }
            else
            {
                using var stream = File.Create(outputPath);
                icon.Save(stream);
            }

            return true;
        }

        if (args[0].Equals("--render-history", StringComparison.OrdinalIgnoreCase))
        {
            var historyNow = DateTimeOffset.UtcNow;
            var start = historyNow.AddDays(-7);
            var samples = Enumerable.Range(0, (7 * 24) + 1)
                .Select(index =>
                {
                    var recordedAt = start.AddHours(index);
                    var primaryAvailable = index == 7 * 24
                        ? 20d
                        : 100d - ((index % 5) * 16d);
                    var weeklyAvailable = index == 7 * 24
                        ? 25d
                        : 92d - (index * 0.28d) + (index >= 96 ? 22d : 0d);
                    return new UsageHistorySample(
                        recordedAt,
                        primaryAvailable,
                        Math.Clamp(weeklyAvailable, 0, 100),
                        recordedAt.AddHours(5 - (index % 5)),
                        start.AddDays(index >= 96 ? 11 : 7));
                })
                .ToArray();

            using var historyForm = new HistoryForm(new AppSettings { Theme = ThemeMode.Dark });
            historyForm.UpdateHistory(samples);
            historyForm.Location = new Point(-10_000, -10_000);
            historyForm.Show();
            Application.DoEvents();
            using var historyBitmap = new Bitmap(historyForm.ClientSize.Width, historyForm.ClientSize.Height);
            historyForm.DrawToBitmap(historyBitmap, new Rectangle(Point.Empty, historyForm.ClientSize));
            historyBitmap.Save(outputPath, ImageFormat.Png);
            historyForm.Hide();
            return true;
        }

        if (!args[0].Equals("--render-preview", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var now = DateTimeOffset.UtcNow;
        var snapshot = new UsageSnapshot(
            "pro",
            new RateLimitWindow(0, now.AddHours(2).AddMinutes(18), TimeSpan.FromHours(5)),
            new RateLimitWindow(17, now.AddDays(4).AddHours(8), TimeSpan.FromDays(7)),
            new CreditsInfo(true, false, 24.50m),
            [
                new AdditionalRateLimit(
                    "GPT-5 Codex Fast",
                    new RateLimitWindow(8, now.AddHours(3), TimeSpan.FromHours(5)),
                    null),
            ],
            now,
            "developer@example.com");

        using var form = new PopupForm(new AppSettings { Theme = ThemeMode.Dark });
        form.UpdateState(snapshot, refreshing: false, error: null);
        form.Location = new Point(-10_000, -10_000);
        form.Show();
        Application.DoEvents();
        using var bitmap = new Bitmap(form.ClientSize.Width, form.ClientSize.Height);
        form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.ClientSize));
        bitmap.Save(outputPath, ImageFormat.Png);
        form.Hide();
        return true;
    }

    private static bool TryReadIconPercent(string argument, out double? availablePercent)
    {
        const string command = "--render-icon";
        availablePercent = 100;
        if (argument.Equals(command, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (argument.Equals(command + "=loading", StringComparison.OrdinalIgnoreCase))
        {
            availablePercent = null;
            return true;
        }

        if (argument.StartsWith(command + "=", StringComparison.OrdinalIgnoreCase)
            && double.TryParse(
                argument[(command.Length + 1)..],
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var parsedPercent))
        {
            availablePercent = parsedPercent;
            return true;
        }

        availablePercent = null;
        return false;
    }
}
