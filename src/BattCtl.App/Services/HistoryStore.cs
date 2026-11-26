using System.Globalization;
using System.Text.Json;

namespace BattCtl.App.Services;

// Reads the daily JSONL files the SYSTEM sampler writes under
// C:\ProgramData\battctl\history. The app never writes; the sampler may append
// while we read, so files open shared and a torn last line is a skipped line,
// not an error.
public sealed class HistoryStore
{
    private const string FilePrefix = "battery-";
    private const string FileSuffix = ".jsonl";
    private const string DateFormat = "yyyyMMdd";

    private static readonly JsonSerializerOptions SerializerOptions = new();

    private readonly string _directory;

    public HistoryStore()
    {
        _directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "battctl",
            "history");
    }

    public string HistoryDirectory => _directory;

    // (newest LastWriteTimeUtc, total bytes) or null when there is nothing to
    // read. A changed value means the sampler has written; the view model uses
    // it to skip redundant reloads.
    public (DateTime LastWriteUtc, long TotalBytes)? GetStamp()
    {
        try
        {
            if (!System.IO.Directory.Exists(_directory))
            {
                return null;
            }

            DateTime newest = DateTime.MinValue;
            long totalBytes = 0;
            var files = ListDayFiles(DateTime.MinValue, DateTime.MaxValue);
            foreach (var file in files)
            {
                var info = new FileInfo(file);
                totalBytes += info.Length;
                if (info.LastWriteTimeUtc > newest)
                {
                    newest = info.LastWriteTimeUtc;
                }
            }

            return files.Count == 0 ? null : (newest, totalBytes);
        }
        catch
        {
            return null;
        }
    }

    public async Task<HistoryLoadResult> LoadAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken)
    {
        if (to < from)
        {
            (from, to) = (to, from);
        }

        if (!System.IO.Directory.Exists(_directory))
        {
            return new HistoryLoadResult { DirectoryMissing = true, RangeEmpty = true };
        }

        // One local day either side absorbs the UTC/local boundary: a line is
        // written under the local day of the sample, but its "t" is UTC.
        var fromLocal = from.LocalDateTime.Date.AddDays(-1);
        var toLocal = to.LocalDateTime.Date.AddDays(1);

        var samples = new List<HistorySample>();
        var files = new List<string>();
        var skippedLines = 0;
        string? errorMessage = null;

        try
        {
            files = ListDayFiles(fromLocal, toLocal);
        }
        catch (Exception ex)
        {
            return new HistoryLoadResult
            {
                DirectoryMissing = false,
                RangeEmpty = true,
                ErrorMessage = Describe(ex, "listing the history directory"),
            };
        }

        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();

            string?[] lines;
            try
            {
                lines = await ReadAllLinesSharedAsync(file, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // One unreadable file (writer holding it, permissions) must not
                // sink the rest of the window.
                errorMessage = Describe(ex, Path.GetFileName(file));
                continue;
            }

            foreach (var line in lines)
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                HistorySample? sample;
                try
                {
                    sample = JsonSerializer.Deserialize<HistorySample>(line, SerializerOptions);
                }
                catch (JsonException)
                {
                    skippedLines++;
                    continue;
                }

                if (sample is null || !TryParseTimestamp(sample.T, out var timestamp))
                {
                    skippedLines++;
                    continue;
                }

                sample.Timestamp = timestamp;
                samples.Add(sample);
            }
        }

        samples.Sort((a, b) => (a.Timestamp ?? DateTimeOffset.MinValue).CompareTo(b.Timestamp ?? DateTimeOffset.MinValue));

        var earliestAvailable = samples.Count > 0 ? samples[0].Timestamp : null;
        var inRange = samples.Where(s => s.Timestamp >= from && s.Timestamp <= to).ToList();

        return new HistoryLoadResult
        {
            Samples = inRange,
            SkippedLines = skippedLines,
            DirectoryMissing = false,
            RangeEmpty = inRange.Count == 0,
            ErrorMessage = errorMessage,
            EarliestAvailable = earliestAvailable,
        };
    }

    // Sorted list of battery-*.jsonl paths whose filename date falls in the
    // (inclusive) local-day window. Files that do not parse as a date are
    // ignored, never touched.
    private List<string> ListDayFiles(DateTime fromLocal, DateTime toLocal)
    {
        var result = new List<string>();
        foreach (var path in System.IO.Directory.EnumerateFiles(_directory, FilePrefix + "*" + FileSuffix))
        {
            var name = Path.GetFileNameWithoutExtension(path);
            if (name.Length != FilePrefix.Length + DateFormat.Length
                || !name.StartsWith(FilePrefix, StringComparison.Ordinal)
                || !DateTime.TryParseExact(
                    name.AsSpan(FilePrefix.Length),
                    DateFormat,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out var day))
            {
                continue;
            }

            if (day >= fromLocal && day <= toLocal)
            {
                result.Add(path);
            }
        }

        result.Sort(StringComparer.Ordinal);
        return result;
    }

    private static async Task<string?[]> ReadAllLinesSharedAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 4096,
            useAsync: true);

        using var reader = new StreamReader(stream);
        var content = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        return content.Split('\n');
    }

    private static bool TryParseTimestamp(string? value, out DateTimeOffset timestamp)
    {
        // "yyyy-MM-ddTHH:mm:ssZ", tolerant of offsets and fractional seconds.
        return DateTimeOffset.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out timestamp);
    }

    private static string Describe(Exception ex, string what)
    {
        return ex switch
        {
            UnauthorizedAccessException => $"Access denied reading {what}.",
            IOException => $"Could not read {what}.",
            _ => $"Could not read {what}.",
        };
    }
}
