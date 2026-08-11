namespace CodexUsage.App.Settings;

public enum ThemeMode
{
    System,
    Dark,
    Light,
}

public sealed class AppSettings
{
    public int RefreshIntervalMinutes { get; set; } = 5;

    public bool NotificationsEnabled { get; set; } = true;

    public int NotificationThresholdPercent { get; set; } = 20;

    public ThemeMode Theme { get; set; } = ThemeMode.System;

    public AppSettings Normalize()
    {
        var allowedIntervals = new[] { 1, 2, 5, 15, 30 };
        if (!allowedIntervals.Contains(RefreshIntervalMinutes))
        {
            RefreshIntervalMinutes = 5;
        }

        // Migrate settings written before v0.2, when thresholds represented percent used.
        if (NotificationThresholdPercent > 50)
        {
            NotificationThresholdPercent = 100 - Math.Clamp(NotificationThresholdPercent, 0, 100);
        }

        var allowedThresholds = new[] { 0, 10, 20, 30 };
        if (!allowedThresholds.Contains(NotificationThresholdPercent))
        {
            NotificationThresholdPercent = 20;
        }

        return this;
    }
}
