using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace CodexUsage.Tokens;

public sealed record CodexTokenUsageSummary(
    long SelectedPeriodTokens,
    long? SinceResetTokens,
    long TodayTokens,
    int SessionFiles);

public sealed class CodexTokenUsageReader
{
    private const int ScanBufferSize = 64 * 1024;
    private const int TimestampPrefixSize = 256;
    private const int MaximumTokenEventSize = 256 * 1024;

    private static readonly byte[] TokenCountMarker = "\"type\":\"token_count\""u8.ToArray();

    private readonly string _codexHome;
    private readonly TimeZoneInfo _timeZone;

    public CodexTokenUsageReader(string? codexHome = null, TimeZoneInfo? timeZone = null)
    {
        _codexHome = codexHome
            ?? Environment.GetEnvironmentVariable("CODEX_HOME")
            ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".codex");
        _timeZone = timeZone ?? TimeZoneInfo.Local;
    }

    public CodexTokenUsageSummary Read(
        DateTimeOffset now,
        DateTimeOffset selectedPeriodStart,
        DateTimeOffset? resetStartedAt,
        CancellationToken cancellationToken = default)
    {
        var today = TimeZoneInfo.ConvertTime(now, _timeZone).Date;
        var todayStart = new DateTimeOffset(today, _timeZone.GetUtcOffset(today));
        var earliestStart = selectedPeriodStart < todayStart ? selectedPeriodStart : todayStart;
        if (resetStartedAt is { } resetStart && resetStart < earliestStart)
        {
            earliestStart = resetStart;
        }

        var selectedPeriodTokens = 0L;
        var sinceResetTokens = 0L;
        var todayTokens = 0L;
        var sessionFiles = 0;
        foreach (var file in EnumerateSessionFiles())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (file.LastWriteTimeUtc < earliestStart.UtcDateTime)
            {
                continue;
            }

            try
            {
                var firstTimestamp = ReadFirstTimestamp(file.FullName);
                var finalRecord = ReadLastTokenRecordBefore(
                    file.FullName,
                    file.Length,
                    ExclusiveUpperBound(now),
                    cancellationToken);
                if (finalRecord is null)
                {
                    continue;
                }

                sessionFiles++;
                selectedPeriodTokens = SaturatingAdd(
                    selectedPeriodTokens,
                    ReadTokensSince(file, firstTimestamp, finalRecord, selectedPeriodStart, cancellationToken));
                todayTokens = SaturatingAdd(
                    todayTokens,
                    ReadTokensSince(file, firstTimestamp, finalRecord, todayStart, cancellationToken));
                if (resetStartedAt is { } currentResetStart)
                {
                    sinceResetTokens = SaturatingAdd(
                        sinceResetTokens,
                        ReadTokensSince(file, firstTimestamp, finalRecord, currentResetStart, cancellationToken));
                }
            }
            catch (Exception exception) when (exception is IOException
                or UnauthorizedAccessException
                or JsonException
                or ArgumentException)
            {
                // Local session logs are best-effort and may be rotated while they are read.
            }
        }

        return new CodexTokenUsageSummary(
            selectedPeriodTokens,
            resetStartedAt is null ? null : sinceResetTokens,
            todayTokens,
            sessionFiles);
    }

    private long ReadTokensSince(
        FileInfo file,
        DateTimeOffset? firstTimestamp,
        TokenRecord finalRecord,
        DateTimeOffset periodStart,
        CancellationToken cancellationToken)
    {
        if (finalRecord.Timestamp < periodStart)
        {
            return 0;
        }

        if (firstTimestamp is null || firstTimestamp >= periodStart)
        {
            return finalRecord.TotalTokens;
        }

        var searchEnd = FindBoundaryOffset(file.FullName, periodStart, cancellationToken);
        var baseline = ReadLastTokenRecordBefore(
            file.FullName,
            searchEnd,
            periodStart,
            cancellationToken);
        return Math.Max(0, finalRecord.TotalTokens - (baseline?.TotalTokens ?? 0));
    }

    private IReadOnlyList<FileInfo> EnumerateSessionFiles()
    {
        var filesByName = new Dictionary<string, FileInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var folderName in new[] { "sessions", "archived_sessions" })
        {
            var folder = Path.Combine(_codexHome, folderName);
            try
            {
                if (!Directory.Exists(folder))
                {
                    continue;
                }

                foreach (var path in Directory.EnumerateFiles(folder, "*.jsonl", SearchOption.AllDirectories))
                {
                    var file = new FileInfo(path);
                    if (!filesByName.TryGetValue(file.Name, out var existing)
                        || file.LastWriteTimeUtc > existing.LastWriteTimeUtc)
                    {
                        filesByName[file.Name] = file;
                    }
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Keep any files that were successfully enumerated.
            }
        }

        return filesByName.Values.ToArray();
    }

    private static long FindBoundaryOffset(
        string path,
        DateTimeOffset cutoff,
        CancellationToken cancellationToken)
    {
        using var stream = OpenRead(path);
        var low = 0L;
        var high = stream.Length;
        var bestAfterCutoff = stream.Length;
        for (var iteration = 0; iteration < 48 && high - low > ScanBufferSize; iteration++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var middle = low + ((high - low) / 2);
            var lineStart = FindNextLineStart(stream, middle, cancellationToken);
            if (lineStart >= stream.Length)
            {
                high = middle;
                continue;
            }

            var timestamp = ReadTimestampAt(stream, lineStart);
            if (timestamp is null)
            {
                low = Math.Min(stream.Length, Math.Max(middle + 1, lineStart + 1));
                continue;
            }

            if (timestamp < cutoff)
            {
                var nextLine = FindNextLineStart(stream, lineStart, cancellationToken);
                low = Math.Min(stream.Length, Math.Max(middle + 1, nextLine));
            }
            else
            {
                bestAfterCutoff = Math.Min(bestAfterCutoff, lineStart);
                high = middle;
            }
        }

        return bestAfterCutoff;
    }

    private static long FindNextLineStart(
        FileStream stream,
        long offset,
        CancellationToken cancellationToken)
    {
        if (offset >= stream.Length)
        {
            return stream.Length;
        }

        var buffer = ArrayPool<byte>.Shared.Rent(ScanBufferSize);
        try
        {
            stream.Position = Math.Max(0, offset);
            while (stream.Position < stream.Length)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var readStart = stream.Position;
                var read = stream.Read(buffer, 0, buffer.Length);
                if (read == 0)
                {
                    break;
                }

                var newline = Array.IndexOf(buffer, (byte)'\n', 0, read);
                if (newline >= 0)
                {
                    return readStart + newline + 1;
                }
            }

            return stream.Length;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static TokenRecord? ReadLastTokenRecordBefore(
        string path,
        long endOffset,
        DateTimeOffset exclusiveCutoff,
        CancellationToken cancellationToken)
    {
        using var stream = OpenRead(path);
        var cursor = Math.Clamp(endOffset, 0, stream.Length);
        var lineEnd = cursor;
        var scanBuffer = ArrayPool<byte>.Shared.Rent(ScanBufferSize);
        try
        {
            while (cursor > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var chunkStart = Math.Max(0, cursor - ScanBufferSize);
                var chunkLength = checked((int)(cursor - chunkStart));
                stream.Position = chunkStart;
                ReadExactly(stream, scanBuffer, chunkLength);
                for (var index = chunkLength - 1; index >= 0; index--)
                {
                    if (scanBuffer[index] != (byte)'\n')
                    {
                        continue;
                    }

                    var lineStart = chunkStart + index + 1;
                    var record = TryReadTokenRecord(stream, lineStart, lineEnd);
                    if (record is not null && record.Timestamp < exclusiveCutoff)
                    {
                        return record;
                    }

                    lineEnd = chunkStart + index;
                }

                cursor = chunkStart;
            }

            var firstRecord = TryReadTokenRecord(stream, 0, lineEnd);
            return firstRecord is not null && firstRecord.Timestamp < exclusiveCutoff
                ? firstRecord
                : null;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(scanBuffer);
        }
    }

    private static TokenRecord? TryReadTokenRecord(FileStream stream, long start, long end)
    {
        var length = end - start;
        if (length <= 0 || length > MaximumTokenEventSize)
        {
            return null;
        }

        var buffer = ArrayPool<byte>.Shared.Rent(checked((int)length));
        try
        {
            stream.Position = start;
            ReadExactly(stream, buffer, checked((int)length));
            var payload = new ReadOnlyMemory<byte>(buffer, 0, checked((int)length));
            if (payload.Span.IndexOf(TokenCountMarker) < 0)
            {
                return null;
            }

            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;
            if (!root.TryGetProperty("timestamp", out var timestampElement)
                || !timestampElement.TryGetDateTimeOffset(out var timestamp)
                || !root.TryGetProperty("payload", out var eventPayload)
                || !eventPayload.TryGetProperty("type", out var type)
                || type.GetString() != "token_count"
                || !eventPayload.TryGetProperty("info", out var info)
                || info.ValueKind != JsonValueKind.Object
                || !info.TryGetProperty("total_token_usage", out var totalUsage)
                || !totalUsage.TryGetProperty("total_tokens", out var totalTokensElement)
                || !totalTokensElement.TryGetInt64(out var totalTokens))
            {
                return null;
            }

            return new TokenRecord(timestamp, Math.Max(0, totalTokens));
        }
        catch (JsonException)
        {
            return null;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static DateTimeOffset? ReadFirstTimestamp(string path)
    {
        using var stream = OpenRead(path);
        return ReadTimestampAt(stream, 0);
    }

    private static DateTimeOffset? ReadTimestampAt(FileStream stream, long offset)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(TimestampPrefixSize);
        try
        {
            stream.Position = offset;
            var read = stream.Read(buffer, 0, TimestampPrefixSize);
            if (read == 0)
            {
                return null;
            }

            var prefix = Encoding.UTF8.GetString(buffer, 0, read);
            const string marker = "\"timestamp\":\"";
            var start = prefix.IndexOf(marker, StringComparison.Ordinal);
            if (start < 0)
            {
                return null;
            }

            start += marker.Length;
            var end = prefix.IndexOf('"', start);
            return end > start
                && DateTimeOffset.TryParse(
                    prefix[start..end],
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal,
                    out var timestamp)
                    ? timestamp
                    : null;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static FileStream OpenRead(string path)
        => new(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            ScanBufferSize,
            FileOptions.RandomAccess);

    private static void ReadExactly(FileStream stream, byte[] buffer, int count)
    {
        var totalRead = 0;
        while (totalRead < count)
        {
            var read = stream.Read(buffer, totalRead, count - totalRead);
            if (read == 0)
            {
                throw new EndOfStreamException();
            }

            totalRead += read;
        }
    }

    private static DateTimeOffset ExclusiveUpperBound(DateTimeOffset timestamp)
        => timestamp < DateTimeOffset.MaxValue ? timestamp.AddTicks(1) : timestamp;

    private static long SaturatingAdd(long first, long second)
        => second > long.MaxValue - first ? long.MaxValue : first + second;

    private sealed record TokenRecord(DateTimeOffset Timestamp, long TotalTokens);
}
