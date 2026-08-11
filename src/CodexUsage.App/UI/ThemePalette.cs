using CodexUsage.App.Settings;
using Microsoft.Win32;

namespace CodexUsage.App.UI;

public sealed record ThemePalette(
    bool IsDark,
    Color Background,
    Color Card,
    Color CardHover,
    Color Text,
    Color SecondaryText,
    Color MutedText,
    Color Border,
    Color Accent,
    Color Success,
    Color Warning,
    Color Danger)
{
    public static ThemePalette Resolve(ThemeMode mode)
    {
        var useDark = mode switch
        {
            ThemeMode.Dark => true,
            ThemeMode.Light => false,
            _ => SystemUsesDarkApps(),
        };

        return useDark
            ? new ThemePalette(
                true,
                Color.FromArgb(24, 24, 27),
                Color.FromArgb(35, 35, 40),
                Color.FromArgb(44, 44, 50),
                Color.FromArgb(246, 246, 247),
                Color.FromArgb(184, 184, 193),
                Color.FromArgb(129, 129, 140),
                Color.FromArgb(58, 58, 65),
                Color.FromArgb(78, 146, 255),
                Color.FromArgb(65, 196, 139),
                Color.FromArgb(245, 173, 66),
                Color.FromArgb(241, 91, 91))
            : new ThemePalette(
                false,
                Color.FromArgb(250, 250, 251),
                Color.White,
                Color.FromArgb(244, 244, 246),
                Color.FromArgb(28, 28, 31),
                Color.FromArgb(85, 85, 94),
                Color.FromArgb(126, 126, 136),
                Color.FromArgb(222, 222, 226),
                Color.FromArgb(30, 101, 224),
                Color.FromArgb(22, 145, 95),
                Color.FromArgb(194, 116, 19),
                Color.FromArgb(206, 48, 48));
    }

    public Color AvailabilityColor(double percent)
    {
        return percent switch
        {
            <= 10 => Danger,
            <= 30 => Warning,
            _ => Success,
        };
    }

    private static bool SystemUsesDarkApps()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int value && value == 0;
        }
        catch
        {
            return true;
        }
    }
}
