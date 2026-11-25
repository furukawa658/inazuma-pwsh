using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace InazumaPwsh;

public sealed class EvidenceCapsuleStore
{
    private const long MaxCapsuleBytes = 20L * 1024 * 1024;
    private const long MaxStoreBytes = 200L * 1024 * 1024;
    private const int MaxCapsules = 100;
    private const int CleanupMaxAgeDays = 3;
    private static readonly Regex SensitivePattern = new(
        @"(?is)(bearer\s+[^\s,;]+|(?:api[_-]?key|access[_-]?token|password|passwd|secret|token)\s*[:=]\s*[^\s,;]+|https?://[^\s?]+\?[^\s]+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(100));
    private readonly object _gate = new();
    private readonly Dictionary<string, List<(int Start, int End)>> _seen = new(StringComparer.Ordinal);
    private readonly string _directory;

    public EvidenceCapsuleStore()
    {
        _directory = Environment.GetEnvironmentVariable("ZUMA_CAPSULE_DIRECTORY")
            ?? Path.Combine(Path.GetTempPath(), "inazuma-pwsh-capsules");
    }

    public string Store(string kind, string? content, string workingDirectory, string? sourcePath, string? label, int maxSummaryChars)
    {
        if (!TryResolveContent(content, sourcePath, out var bytes, out var contentError))
            return JsonDefaults.Serialize(new { status = "error", message = contentError });
        if (bytes.Length == 0)
            return JsonDefaults.Serialize(new { status = "error", message = "Capsule content must not be empty." });
        if (bytes.Length > MaxCapsuleBytes)
            return JsonDefaults.Serialize(new { status = "error", message = $"Capsule content exceeds {MaxCapsuleBytes} bytes." });
        if (!IsSupportedKind(kind))
            return JsonDefaults.Serialize(new { status = "error", message = "kind must be file, command-output, test-report, or log." });
        if (string.IsNullOrWhiteSpace(workingDirectory) || !Path.IsPathRooted(workingDirectory))
            return JsonDefaults.Serialize(new { status = "error", message = "workingDirectory must be an absolute path." });

        var text = Decode(bytes);
        var lines = text.Replace("\r\n", "\n").Split('\n');
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(kind + "\0" + text))).ToLowerInvariant();
        var capsuleId = hash[..24];
        var capsulePath = GetCapsulePath(capsuleId, "txt");
        var metadataPath = GetCapsulePath(capsuleId, "json");
        bool reused;

        lock (_gate)
        {
            Directory.CreateDirectory(_directory);
            Cleanup();
            reused = File.Exists(capsulePath) && File.Exists(metadataPath);
            if (!reused)
            {
                File.WriteAllText(capsulePath, text, new UTF8Encoding(false));
                var metadata = new CapsuleMetadata
                {
                    CapsuleId = capsuleId,
                    Kind = kind,
                    SourceHash = hash,
                    SourcePath = sourcePath is null ? null : Path.GetFullPath(sourcePath),
                    WorkingDirectory = Path.GetFullPath(workingDirectory),
                    Label = CleanLabel(label),
                    CreatedAt = DateTimeOffset.UtcNow
                };
                File.WriteAllText(metadataPath, JsonSerializer.Serialize(metadata, JsonDefaults.Options), new UTF8Encoding(false));
            }
            Cleanup();
        }

        var summaryLimit = Math.Clamp(maxSummaryChars, 80, 4000);
        var summary = $"Stored {kind} capsule {capsuleId}: {lines.Length} lines, {bytes.Length} bytes.";
        return JsonDefaults.Serialize(new
        {
            status = "success",
            capsuleId,
            kind,
            sourceHash = hash,
            summary = summary[..Math.Min(summary.Length, summaryLimit)],
            lineCount = lines.Length,
            charCount = text.Length,
            queryHint = "Use query_capsule with this capsuleId and a line range, regular expression, or baseCapsuleId.",
            createdAt = ReadMetadata(metadataPath)?.CreatedAt,
            reused
        });
    }

    public string Query(string capsuleId, string? baseCapsuleId, string? query, int? startLine, int? endLine, bool onlyUnseen, int maxChars)
    {
        if (!IsCapsuleId(capsuleId))
            return JsonDefaults.Serialize(new { status = "error", message = "capsuleId is invalid." });
        var path = GetCapsulePath(capsuleId, "txt");
        if (!File.Exists(path))
            return JsonDefaults.Serialize(new { status = "error", message = "Capsule was not found or has expired." });

        string text;
        string[] lines;
        try
        {
            text = File.ReadAllText(path, Encoding.UTF8);
            lines = text.Replace("\r\n", "\n").Split('\n');
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return JsonDefaults.Serialize(new { status = "error", message = ex.Message });
        }

        try
        {
            if (SensitivePattern.IsMatch(text))
            {
                return JsonDefaults.Serialize(new
                {
                    status = "sensitive",
                    capsuleId,
                    matches = Array.Empty<object>(),
                    unseenChars = 0,
                    duplicateCharsOmitted = 0,
                    hasMore = false,
                    nextLine = (int?)null,
                    message = "Capsule contains a possible credential or sensitive URL and is available only in local storage."
                });
            }
        }
        catch (RegexMatchTimeoutException)
        {
            return JsonDefaults.Serialize(new { status = "error", message = "Sensitive-content check exceeded 100 ms." });
        }

        Regex? expression = null;
        if (!string.IsNullOrWhiteSpace(query))
        {
            try { expression = new Regex(query, RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100)); }
            catch (ArgumentException ex) { return JsonDefaults.Serialize(new { status = "error", message = $"Invalid query: {ex.Message}" }); }
            catch (RegexMatchTimeoutException) { return JsonDefaults.Serialize(new { status = "error", message = "query exceeded 100 ms." }); }
        }

        var selected = BuildSelection(lines, capsuleId, baseCapsuleId, expression, startLine, endLine);
        if (selected.Error is not null)
            return JsonDefaults.Serialize(new { status = "error", message = selected.Error });

        int max = Math.Clamp(maxChars, 1000, 50000);
        var matches = new List<object>();
        var returnedLines = new List<int>();
        var omitted = 0;
        var visibleChars = 0;
        int? nextLine = null;

        lock (_gate)
        {
            _seen.TryGetValue(capsuleId, out var seen);
            seen ??= [];
            foreach (var lineNumber in selected.Lines)
            {
                var alreadySeen = onlyUnseen && ContainsLine(seen, lineNumber);
                var lineText = lines[lineNumber - 1];
                if (alreadySeen)
                {
                    omitted += lineText.Length;
                    continue;
                }
                var separator = matches.Count == 0 ? 0 : 1;
                if (visibleChars + separator + lineText.Length > max)
                {
                    nextLine = lineNumber;
                    break;
                }
                matches.Add(new { line = lineNumber, text = lineText });
                returnedLines.Add(lineNumber);
                visibleChars += separator + lineText.Length;
            }
            if (onlyUnseen && returnedLines.Count > 0)
            {
                foreach (var lineNumber in returnedLines) AddSeenLine(seen, lineNumber);
                _seen[capsuleId] = seen;
            }
        }

        return JsonDefaults.Serialize(new
        {
            status = "success",
            capsuleId,
            matches,
            unseenChars = visibleChars,
            duplicateCharsOmitted = omitted,
            hasMore = nextLine.HasValue,
            nextLine
        });
    }

    private CapsuleSelection BuildSelection(string[] lines, string capsuleId, string? baseCapsuleId, Regex? expression, int? startLine, int? endLine)
    {
        IEnumerable<int> selection;
        if (expression is not null)
        {
            var matches = new List<int>();
            for (var lineNumber = 1; lineNumber <= lines.Length; lineNumber++)
            {
                try
                {
                    if (expression.IsMatch(lines[lineNumber - 1])) matches.Add(lineNumber);
                }
                catch (RegexMatchTimeoutException)
                {
                    return CapsuleSelection.Failed("query exceeded 100 ms.");
                }
            }
            selection = matches;
        }
        else if (baseCapsuleId is not null && !startLine.HasValue && !endLine.HasValue)
        {
            if (!IsCapsuleId(baseCapsuleId)) return CapsuleSelection.Failed("baseCapsuleId is invalid.");
            var basePath = GetCapsulePath(baseCapsuleId, "txt");
            if (!File.Exists(basePath)) return CapsuleSelection.Failed("Base capsule was not found or has expired.");
            var baseLines = File.ReadAllText(basePath, Encoding.UTF8).Replace("\r\n", "\n").Split('\n');
            var prefix = 0;
            while (prefix < lines.Length && prefix < baseLines.Length && string.Equals(lines[prefix], baseLines[prefix], StringComparison.Ordinal)) prefix++;
            selection = Enumerable.Range(prefix + 1, Math.Max(0, lines.Length - prefix));
        }
        else
        {
            var start = startLine ?? 1;
            var end = endLine ?? start + 199;
            if (start < 1 || end < start) return CapsuleSelection.Failed("startLine and endLine are invalid.");
            start = Math.Min(start, lines.Length);
            end = Math.Min(end, lines.Length);
            selection = Enumerable.Range(start, Math.Max(0, end - start + 1));
        }

        return CapsuleSelection.Create(selection.ToArray());
    }

    private static bool TryResolveContent(string? content, string? sourcePath, out byte[] bytes, out string? error)
    {
        bytes = [];
        error = null;
        if (!string.IsNullOrEmpty(content) && !string.IsNullOrEmpty(sourcePath))
        {
            error = "Specify either content or sourcePath, not both.";
            return false;
        }
        if (!string.IsNullOrEmpty(sourcePath))
        {
            if (!Path.IsPathRooted(sourcePath) || !File.Exists(sourcePath))
            {
                error = "sourcePath must be an existing absolute file path.";
                return false;
            }
            try
            {
                bytes = File.ReadAllBytes(sourcePath);
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                error = ex.Message;
                return false;
            }
        }
        if (content is not null)
        {
            bytes = Encoding.UTF8.GetBytes(content);
            return true;
        }
        error = "Specify content or sourcePath.";
        return false;
    }

    private void Cleanup()
    {
        var now = DateTime.UtcNow;
        var entries = Directory.EnumerateFiles(_directory, "*.txt")
            .Select(path => new
            {
                Path = path,
                Id = Path.GetFileNameWithoutExtension(path),
                LastWrite = File.GetLastWriteTimeUtc(path),
                Length = new FileInfo(path).Length
            })
            .OrderBy(entry => entry.LastWrite)
            .ToArray();

        foreach (var expired in entries.Where(entry => now - entry.LastWrite > TimeSpan.FromDays(CleanupMaxAgeDays)))
            Delete(expired.Path, expired.Id);

        var remaining = entries.Where(entry => File.Exists(entry.Path)).ToArray();
        var excessCount = Math.Max(0, remaining.Length - MaxCapsules);
        foreach (var entry in remaining.Take(excessCount)) Delete(entry.Path, entry.Id);

        var total = Directory.EnumerateFiles(_directory, "*.txt").Sum(path => new FileInfo(path).Length);
        if (total <= MaxStoreBytes) return;
        foreach (var entry in remaining.Where(entry => File.Exists(entry.Path)))
        {
            Delete(entry.Path, entry.Id);
            total = Directory.EnumerateFiles(_directory, "*.txt").Sum(path => new FileInfo(path).Length);
            if (total <= MaxStoreBytes) break;
        }
    }

    private void Delete(string contentPath, string capsuleId)
    {
        try { File.Delete(contentPath); } catch (IOException) { }
        try { File.Delete(GetCapsulePath(capsuleId, "json")); } catch (IOException) { }
        _seen.Remove(capsuleId);
    }

    private static bool IsSupportedKind(string kind) => kind is "file" or "command-output" or "test-report" or "log";
    private static bool IsCapsuleId(string value) => Regex.IsMatch(value, "^[0-9a-f]{24}$", RegexOptions.CultureInvariant);
    private string GetCapsulePath(string capsuleId, string extension) => Path.Combine(_directory, $"{capsuleId}.{extension}");
    private static string Decode(byte[] bytes) => Encoding.UTF8.GetString(bytes);
    private static string? CleanLabel(string? label) => string.IsNullOrWhiteSpace(label) ? null : label.Trim()[..Math.Min(label.Trim().Length, 200)];
    private static CapsuleMetadata? ReadMetadata(string path) => File.Exists(path) ? JsonSerializer.Deserialize<CapsuleMetadata>(File.ReadAllText(path), JsonDefaults.Options) : null;
    private static bool ContainsLine(IReadOnlyCollection<(int Start, int End)> ranges, int line) => ranges.Any(range => line >= range.Start && line <= range.End);

    private static void AddSeenLine(List<(int Start, int End)> ranges, int line)
    {
        for (var index = 0; index < ranges.Count; index++)
        {
            var range = ranges[index];
            if (line < range.Start - 1 || line > range.End + 1) continue;
            ranges[index] = (Math.Min(range.Start, line), Math.Max(range.End, line));
            return;
        }
        ranges.Add((line, line));
    }

    private sealed class CapsuleMetadata
    {
        public required string CapsuleId { get; init; }
        public required string Kind { get; init; }
        public required string SourceHash { get; init; }
        public string? SourcePath { get; init; }
        public string? WorkingDirectory { get; init; }
        public string? Label { get; init; }
        public DateTimeOffset CreatedAt { get; init; }
    }

    private sealed record CapsuleSelection(int[] Lines, string? Error)
    {
        public static CapsuleSelection Create(int[] lines) => new(lines, null);
        public static CapsuleSelection Failed(string error) => new([], error);
    }
}
