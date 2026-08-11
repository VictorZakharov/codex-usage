using System.Text.Json;
using CodexUsage.Models;

namespace CodexUsage.History;

public sealed class UsageHistoryStore
{
    public const int MaximumSamples = 50_000;

    public static readonly TimeSpan Retention = TimeSpan.FromDays(90);

    private static readonly TimeSpan UnchangedHeartbeat = TimeSpan.FromMinutes(30);

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly object _gate = new();
    private readonly string _filePath;
    private List<UsageHistorySample>? _samples;

    public UsageHistoryStore(string? filePath = null)
    {
        _filePath = filePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CodexUsage",
            "history.jsonl");
    }

    public string FilePath => _filePath;

    public IReadOnlyList<UsageHistorySample> Load()
    {
        lock (_gate)
        {
            EnsureLoaded();
            return _samples!.ToArray();
        }
    }

    public IReadOnlyList<UsageHistorySample> Record(UsageSnapshot snapshot)
    {
        lock (_gate)
        {
            EnsureLoaded();
            var sample = Normalize(UsageHistorySample.FromSnapshot(snapshot));
            if (!ShouldRecord(sample))
            {
                return _samples!.ToArray();
            }

            _samples!.Add(sample);
            var pruned = Prune(sample.RecordedAt);
            if (pruned)
            {
                TryRewrite();
            }
            else
            {
                TryAppend(sample);
            }

            return _samples.ToArray();
        }
    }

    private void EnsureLoaded()
    {
        if (_samples is not null)
        {
            return;
        }

        _samples = [];
        try
        {
            if (!File.Exists(_filePath))
            {
                return;
            }

            foreach (var line in File.ReadLines(_filePath))
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                try
                {
                    var sample = JsonSerializer.Deserialize<UsageHistorySample>(line, SerializerOptions);
                    if (sample is not null && sample.RecordedAt != default)
                    {
                        _samples.Add(Normalize(sample));
                    }
                }
                catch (JsonException)
                {
                    // A damaged line should not hide the rest of the history.
                }
            }

            _samples = _samples
                .OrderBy(sample => sample.RecordedAt)
                .GroupBy(sample => sample.RecordedAt)
                .Select(group => group.Last())
                .ToList();
            if (_samples.Count > 0 && Prune(DateTimeOffset.UtcNow))
            {
                TryRewrite();
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _samples = [];
        }
    }

    private bool ShouldRecord(UsageHistorySample sample)
    {
        if (_samples!.Count == 0)
        {
            return true;
        }

        var previous = _samples[^1];
        if (sample.RecordedAt <= previous.RecordedAt)
        {
            return false;
        }

        return sample.RecordedAt - previous.RecordedAt >= UnchangedHeartbeat
            || ValueChanged(previous.PrimaryAvailablePercent, sample.PrimaryAvailablePercent)
            || ValueChanged(previous.SecondaryAvailablePercent, sample.SecondaryAvailablePercent)
            || previous.PrimaryResetsAt != sample.PrimaryResetsAt
            || previous.SecondaryResetsAt != sample.SecondaryResetsAt;
    }

    private bool Prune(DateTimeOffset referenceTime)
    {
        var previousCount = _samples!.Count;
        var cutoff = referenceTime - Retention;
        _samples.RemoveAll(sample => sample.RecordedAt < cutoff);
        if (_samples.Count > MaximumSamples)
        {
            _samples.RemoveRange(0, _samples.Count - MaximumSamples);
        }

        return previousCount != _samples.Count;
    }

    private void TryAppend(UsageHistorySample sample)
    {
        try
        {
            var directory = Path.GetDirectoryName(_filePath)
                ?? throw new IOException("History path has no parent directory.");
            Directory.CreateDirectory(directory);
            File.AppendAllText(_filePath, JsonSerializer.Serialize(sample, SerializerOptions) + Environment.NewLine);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // History is best-effort and must never prevent a usage refresh.
        }
    }

    private void TryRewrite()
    {
        var temporaryPath = _filePath + ".tmp";
        try
        {
            var directory = Path.GetDirectoryName(_filePath)
                ?? throw new IOException("History path has no parent directory.");
            Directory.CreateDirectory(directory);
            File.WriteAllLines(
                temporaryPath,
                _samples!.Select(sample => JsonSerializer.Serialize(sample, SerializerOptions)));
            File.Move(temporaryPath, _filePath, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            try
            {
                File.Delete(temporaryPath);
            }
            catch (Exception cleanupException) when (cleanupException is IOException or UnauthorizedAccessException)
            {
                // Nothing else to do for best-effort history persistence.
            }
        }
    }

    private static bool ValueChanged(double? first, double? second)
    {
        if (first is null || second is null)
        {
            return first != second;
        }

        return Math.Abs(first.Value - second.Value) >= 0.01d;
    }

    private static UsageHistorySample Normalize(UsageHistorySample sample)
        => sample with
        {
            PrimaryAvailablePercent = Clamp(sample.PrimaryAvailablePercent),
            SecondaryAvailablePercent = Clamp(sample.SecondaryAvailablePercent),
        };

    private static double? Clamp(double? value)
        => value is null || !double.IsFinite(value.Value)
            ? null
            : Math.Clamp(value.Value, 0, 100);
}
