using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace InazumaPwsh;

public sealed class ContextStateStore
{
    private static readonly Regex SecretPattern = new(
        @"(?is)(bearer\s+[^\s,;]+|(?:api[_-]?key|password|passwd|secret|token)\s*[:=]\s*[^\s,;]+|https?://[^\s?]+\?[^\s]+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(100));
    private readonly object _gate = new();
    private readonly string _directory;

    public ContextStateStore()
    {
        _directory = Environment.GetEnvironmentVariable("ZUMA_CONTEXT_DIRECTORY")
            ?? Path.Combine(Path.GetTempPath(), "inazuma-pwsh-context");
    }

    public string Remember(string workingDirectory, string kind, string key, string value, string? evidence)
    {
        var error = ValidateCommon(workingDirectory, kind, key, value);
        if (error != null) return Error(error);

        lock (_gate)
        {
            var records = ReadRecords(workingDirectory);
            if (kind == "invalidated")
            {
                foreach (var existingRecord in records.Where(existingRecord => existingRecord.Key.Equals(key, StringComparison.Ordinal) && existingRecord.Kind != "invalidated"))
                    existingRecord.Active = false;
            }
            else
            {
                foreach (var existingRecord in records.Where(existingRecord => existingRecord.Key.Equals(key, StringComparison.Ordinal) && existingRecord.Kind.Equals(kind, StringComparison.Ordinal)))
                    existingRecord.Active = false;
            }

            var createdAt = DateTimeOffset.UtcNow;
            var record = new ContextRecord
            {
                Id = Guid.NewGuid().ToString("N")[..20],
                Kind = kind,
                Key = key.Trim(),
                Value = Redact(value.Trim()),
                Evidence = string.IsNullOrWhiteSpace(evidence) ? null : Redact(evidence.Trim()),
                CreatedAt = createdAt,
                Active = kind != "invalidated"
            };
            records.Add(record);
            WriteRecords(workingDirectory, records);
            return JsonDefaults.Serialize(new
            {
                status = "success",
                factId = record.Id,
                record.Kind,
                record.Key,
                record.CreatedAt,
                active = record.Active
            });
        }
    }

    public string Query(string workingDirectory, string? query, IReadOnlyCollection<string>? kinds, bool onlyActive, int maxResults, int maxChars)
    {
        if (string.IsNullOrWhiteSpace(workingDirectory) || !Path.IsPathRooted(workingDirectory))
            return Error("workingDirectory must be an absolute path.");
        maxResults = Math.Clamp(maxResults, 1, 50);
        maxChars = Math.Clamp(maxChars, 500, 12000);
        var selectedKinds = kinds?.Where(ContextRecord.IsSupportedKind).ToHashSet(StringComparer.Ordinal) ?? [];
        var terms = Tokenize(query);

        List<ContextRecord> candidates;
        lock (_gate)
        {
            candidates = ReadRecords(workingDirectory)
                .Where(record => (!onlyActive || record.Active) && (selectedKinds.Count == 0 || selectedKinds.Contains(record.Kind)))
                .Where(record => terms.Count == 0 || terms.Any(term => SearchText(record).Contains(term, StringComparison.OrdinalIgnoreCase)))
                .OrderByDescending(record => record.CreatedAt)
                .Take(maxResults)
                .ToList();
        }

        var responseChars = 0;
        var matches = new List<object>();
        foreach (var record in candidates)
        {
            var valueChars = Math.Min(record.Value.Length, Math.Max(80, maxChars / 4));
            var item = new
            {
                factId = record.Id,
                kind = record.Kind,
                key = record.Key,
                value = record.Value[..valueChars],
                evidence = record.Evidence,
                record.Active,
                createdAt = record.CreatedAt
            };
            var serialized = JsonDefaults.Serialize(item);
            if (matches.Count > 0 && responseChars + serialized.Length > maxChars) break;
            matches.Add(item);
            responseChars += serialized.Length;
        }

        return JsonDefaults.Serialize(new
        {
            status = "success",
            matches,
            matchCount = matches.Count,
            totalCandidates = candidates.Count,
            onlyActive,
            maxChars
        });
    }

    public string Compile(string workingDirectory, string goal, bool includeEvidence, int maxChars)
    {
        if (string.IsNullOrWhiteSpace(goal)) return Error("goal must not be empty.");
        if (string.IsNullOrWhiteSpace(workingDirectory) || !Path.IsPathRooted(workingDirectory))
            return Error("workingDirectory must be an absolute path.");
        maxChars = Math.Clamp(maxChars, 500, 12000);

        List<ContextRecord> records;
        lock (_gate) records = ReadRecords(workingDirectory);

        var builder = new StringBuilder();
        builder.Append("Goal: ").AppendLine(Redact(goal.Trim()));
        AppendSection(builder, records, "fact", "Facts", includeEvidence, maxChars);
        AppendSection(builder, records, "decision", "Decisions", includeEvidence, maxChars);
        AppendSection(builder, records, "unknown", "Unknowns", includeEvidence, maxChars);
        AppendSection(builder, records, "next-action", "NextAction", includeEvidence, maxChars);
        AppendSection(builder, records, "invalidated", "Invalidated", includeEvidence, maxChars);
        var text = builder.ToString();
        var truncated = text.Length > maxChars;
        if (truncated) text = text[..maxChars];

        return JsonDefaults.Serialize(new
        {
            status = "success",
            text,
            charCount = text.Length,
            isTruncated = truncated,
            activeFactCount = records.Count(record => record.Active),
            evidenceRefCount = records.Count(record => record.Evidence != null),
            maxChars
        });
    }

    private static void AppendSection(StringBuilder builder, List<ContextRecord> records, string kind, string title, bool includeEvidence, int budget)
    {
        var items = records
            .Where(record => record.Kind == kind && (kind == "invalidated" || record.Active))
            .OrderByDescending(record => record.CreatedAt)
            .Take(kind == "invalidated" ? 8 : 20)
            .ToList();
        if (items.Count == 0) return;
        builder.AppendLine().AppendLine($"{title}:");
        foreach (var item in items)
        {
            if (builder.Length + item.Value.Length > budget) break;
            builder.Append("- ").Append(item.Key).Append(": ").Append(item.Value);
            if (includeEvidence && item.Evidence != null) builder.Append(" [evidence: ").Append(item.Evidence).Append(']');
            builder.AppendLine();
        }
    }

    private string PathFor(string workingDirectory)
    {
        Directory.CreateDirectory(_directory);
        var workspace = Path.GetFullPath(workingDirectory).ToLowerInvariant();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(workspace))).ToLowerInvariant()[..24];
        return Path.Combine(_directory, $"{hash}.jsonl");
    }

    private List<ContextRecord> ReadRecords(string workingDirectory)
    {
        var path = PathFor(workingDirectory);
        if (!File.Exists(path)) return [];
        var records = new List<ContextRecord>();
        foreach (var line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                var record = JsonSerializer.Deserialize<ContextRecord>(line, JsonDefaults.Options);
                if (record != null) records.Add(record);
            }
            catch (JsonException) { }
        }
        return records;
    }

    private void WriteRecords(string workingDirectory, IReadOnlyCollection<ContextRecord> records)
    {
        var path = PathFor(workingDirectory);
        var tempPath = path + ".tmp";
        var content = string.Join(Environment.NewLine, records.Select(record => JsonSerializer.Serialize(record, JsonDefaults.Options)));
        File.WriteAllText(tempPath, content + Environment.NewLine, new UTF8Encoding(false));
        File.Move(tempPath, path, true);
    }

    private static string SearchText(ContextRecord record) => $"{record.Key}\n{record.Value}\n{record.Evidence}";

    private static List<string> Tokenize(string? text) => string.IsNullOrWhiteSpace(text)
        ? []
        : Regex.Split(text.Trim().ToLowerInvariant(), @"\W+", RegexOptions.CultureInvariant)
            .Where(token => token.Length >= 2)
            .Distinct(StringComparer.Ordinal)
            .ToList();

    private static string? ValidateCommon(string workingDirectory, string kind, string key, string value)
    {
        if (string.IsNullOrWhiteSpace(workingDirectory) || !Path.IsPathRooted(workingDirectory)) return "workingDirectory must be an absolute path.";
        if (!ContextRecord.IsSupportedKind(kind)) return "kind must be fact, decision, unknown, next-action, or invalidated.";
        if (string.IsNullOrWhiteSpace(key) || key.Length > 200) return "key must contain 1 to 200 characters.";
        if (string.IsNullOrWhiteSpace(value) || value.Length > 12000) return "value must contain 1 to 12000 characters.";
        return null;
    }

    private static string Redact(string value)
    {
        try { return SecretPattern.Replace(value, "[REDACTED]"); }
        catch (RegexMatchTimeoutException) { return "[CONTENT_OMITTED]"; }
    }

    private static string Error(string message) => JsonDefaults.Serialize(new { status = "error", message });

    private sealed class ContextRecord
    {
        public string Id { get; set; } = string.Empty;
        public string Kind { get; set; } = string.Empty;
        public string Key { get; set; } = string.Empty;
        public string Value { get; set; } = string.Empty;
        public string? Evidence { get; set; }
        public DateTimeOffset CreatedAt { get; set; }
        public bool Active { get; set; }

        public static bool IsSupportedKind(string value) => value is "fact" or "decision" or "unknown" or "next-action" or "invalidated";
    }
}
