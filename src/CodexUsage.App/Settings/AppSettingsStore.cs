using System.Text.Json;

namespace CodexUsage.App.Settings;

public sealed class AppSettingsStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
    };

    private readonly string _filePath;

    public AppSettingsStore()
    {
        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CodexUsage");
        _filePath = Path.Combine(directory, "settings.json");
    }

    public AppSettings Load()
    {
        try
        {
            if (!File.Exists(_filePath))
            {
                return new AppSettings();
            }

            var json = File.ReadAllText(_filePath);
            return (JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings()).Normalize();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return new AppSettings();
        }
    }

    public void Save(AppSettings settings)
    {
        var directory = Path.GetDirectoryName(_filePath)
            ?? throw new IOException("Settings path has no parent directory.");
        Directory.CreateDirectory(directory);
        var json = JsonSerializer.Serialize(settings.Normalize(), SerializerOptions);
        File.WriteAllText(_filePath, json);
    }
}
