using System.ComponentModel;
using System.Text;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using ModelContextProtocol.Server;

namespace InazumaPwsh;

[McpServerToolType]
public sealed class FileReadingTools
{
    private sealed class LogGroup
    {
        [JsonPropertyName("count")]
        public int Count { get; set; }

        [JsonPropertyName("firstLine")]
        public int FirstLine { get; set; }

        [JsonPropertyName("lastLine")]
        public int LastLine { get; set; }

        [JsonPropertyName("text")]
        public string Text { get; set; } = string.Empty;

        [JsonPropertyName("context")]
        public List<string> Context { get; set; } = [];
    }

    [McpServerTool(Name = "peek_file")]
    [Description("Return a compact outline of a text file: line count, kind, and line ranges of headings, declarations or top-level keys. Use it before reading a large file, then read only the needed range with read_section. If recommend is FULL the file is small enough to read entirely.")]
    public Task<string> PeekFileAsync(
        [Description("Absolute path to the text file.")] string path,
        [Description("Maximum line count for recommending full read (1 to 5000, default 200).")] int fullThreshold = 200,
        [Description("Maximum number of outline entries to return (10 to 1000, default 200).")] int maxEntries = 200,
        CancellationToken cancellationToken = default)
    {
        var validationError = ValidateFile(path);
        if (validationError != null) return Task.FromResult(validationError);

        try
        {
            var (lines, encoding, bytes) = FileOutline.ReadAndDecodeFile(path);
            string kind = FileOutline.DetermineKind(path);
            var allEntries = FileOutline.GetOutline(path, lines, kind);

            int clampedThreshold = Math.Clamp(fullThreshold, 1, 5000);
            int clampedMaxEntries = Math.Clamp(maxEntries, 10, 1000);

            string recommend = lines.Length <= clampedThreshold ? "FULL" : "PARTIAL";
            bool entriesTruncated = allEntries.Count > clampedMaxEntries;
            var entries = allEntries.Take(clampedMaxEntries).ToList();

            var result = new
            {
                status = "success",
                path,
                kind,
                encoding,
                lines = lines.Length,
                bytes,
                recommend,
                entries,
                entriesTruncated
            };

            return Task.FromResult(JsonDefaults.Serialize(result));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return Task.FromResult(JsonDefaults.Serialize(new { status = "error", message = ex.Message }));
        }
    }

    [McpServerTool(Name = "read_section")]
    [Description("Read only part of a text file with line numbers. Pass query to read the first outline entry whose title contains it (case-insensitive), or pass startLine and endLine. Long ranges are cut at maxChars; continue from nextLine.")]
    public Task<string> ReadSectionAsync(
        [Description("Absolute path to the text file.")] string path,
        [Description("Optional search string to find an outline entry by title.")] string? query = null,
        [Description("1-based line number to start reading from.")] int? startLine = null,
        [Description("1-based line number to end reading at (inclusive). Defaults to startLine + 199.")] int? endLine = null,
        [Description("Maximum characters of formatted text to return (clamped 1000..50000, default 12000).")] int maxChars = 12000,
        CancellationToken cancellationToken = default)
    {
        var validationError = ValidateFile(path);
        if (validationError != null) return Task.FromResult(validationError);

        bool hasQuery = !string.IsNullOrEmpty(query);
        bool hasStart = startLine.HasValue;

        if ((hasQuery && hasStart) || (!hasQuery && !hasStart))
        {
            return Task.FromResult(JsonDefaults.Serialize(new
            {
                status = "error",
                message = "Specify exactly one of 'query' or 'startLine'."
            }));
        }

        try
        {
            var (lines, encoding, _) = FileOutline.ReadAndDecodeFile(path);
            int totalLines = lines.Length;

            int targetStart;
            int targetEnd;
            string? matchedTitle = null;
            object[]? otherMatches = null;

            if (hasQuery)
            {
                var allEntries = FileOutline.GetOutline(path, lines);
                var matches = allEntries
                    .Where(e => e.Title.Contains(query!, StringComparison.OrdinalIgnoreCase))
                    .ToList();

                if (matches.Count == 0)
                {
                    var candidates = allEntries.Take(20).Select(e => e.Title).ToArray();
                    return Task.FromResult(JsonDefaults.Serialize(new
                    {
                        status = "error",
                        message = "No outline entry matched.",
                        candidates
                    }));
                }

                var first = matches[0];
                targetStart = first.Start;
                targetEnd = first.End;
                matchedTitle = first.Title;

                if (matches.Count > 1)
                {
                    otherMatches = matches.Skip(1)
                        .Take(10)
                        .Select(e => (object)new { start = e.Start, title = e.Title })
                        .ToArray();
                }
            }
            else
            {
                targetStart = startLine!.Value;
                targetEnd = endLine ?? (targetStart + 199);
            }

            int clampedMaxChars = Math.Clamp(maxChars, 1000, 50000);

            if (totalLines == 0)
            {
                return Task.FromResult(JsonDefaults.Serialize(new
                {
                    status = "success",
                    path,
                    encoding,
                    totalLines = 0,
                    startLine = 1,
                    endLine = 0,
                    truncated = false,
                    nextLine = (int?)null,
                    text = string.Empty
                }));
            }

            int actualStart = Math.Clamp(targetStart, 1, totalLines);
            int actualEnd = Math.Clamp(targetEnd, actualStart, totalLines);

            var sb = new StringBuilder();
            int lastLineReturned = actualStart;
            bool truncated = false;
            int? nextLine = null;

            for (int lineNum = actualStart; lineNum <= actualEnd; lineNum++)
            {
                string lineContent = lines[lineNum - 1];
                string formattedLine = $"{lineNum}: {lineContent}";

                if (sb.Length == 0)
                {
                    if (formattedLine.Length > clampedMaxChars)
                    {
                        sb.Append(formattedLine.Substring(0, clampedMaxChars));
                        lastLineReturned = lineNum;
                        truncated = true;
                        nextLine = lineNum + 1 <= actualEnd ? lineNum + 1 : (actualEnd < totalLines ? actualEnd + 1 : null);
                        break;
                    }
                    else
                    {
                        sb.Append(formattedLine);
                        lastLineReturned = lineNum;
                    }
                }
                else
                {
                    if (sb.Length + 1 + formattedLine.Length > clampedMaxChars)
                    {
                        truncated = true;
                        nextLine = lineNum;
                        break;
                    }
                    else
                    {
                        sb.Append('\n').Append(formattedLine);
                        lastLineReturned = lineNum;
                    }
                }
            }

            if (hasQuery)
            {
                if (otherMatches != null && otherMatches.Length > 0)
                {
                    return Task.FromResult(JsonDefaults.Serialize(new
                    {
                        status = "success",
                        path,
                        encoding,
                        totalLines,
                        startLine = actualStart,
                        endLine = lastLineReturned,
                        title = matchedTitle,
                        truncated,
                        nextLine,
                        text = sb.ToString(),
                        otherMatches
                    }));
                }

                return Task.FromResult(JsonDefaults.Serialize(new
                {
                    status = "success",
                    path,
                    encoding,
                    totalLines,
                    startLine = actualStart,
                    endLine = lastLineReturned,
                    title = matchedTitle,
                    truncated,
                    nextLine,
                    text = sb.ToString()
                }));
            }

            return Task.FromResult(JsonDefaults.Serialize(new
            {
                status = "success",
                path,
                encoding,
                totalLines,
                startLine = actualStart,
                endLine = lastLineReturned,
                truncated,
                nextLine,
                text = sb.ToString()
            }));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return Task.FromResult(JsonDefaults.Serialize(new { status = "error", message = ex.Message }));
        }
    }

    [McpServerTool(Name = "summarize_log")]
    [Description("Summarize a large log without reading all of it: lines matching pattern (case-insensitive regex) grouped by text with digits normalized, with occurrence counts, first and last line numbers and context, plus the last lines of the file. Use it for build logs and fullLogPath files.")]
    public Task<string> SummarizeLogAsync(
        [Description("Absolute path to the log file.")] string path,
        [Description("Regex pattern to match error lines (default 'error|fail|exception|fatal|warn').")] string pattern = "error|fail|exception|fatal|warn",
        [Description("Context lines before and after the first occurrence (clamped 0..5, default 1).")] int contextLines = 1,
        [Description("Number of tail lines from the end of the file (clamped 0..200, default 20).")] int tailLines = 20,
        [Description("Maximum number of groups to return (clamped 1..200, default 30).")] int maxGroups = 30,
        [Description("Maximum characters of serialized output JSON (clamped 2000..50000, default 8000).")] int maxChars = 8000,
        CancellationToken cancellationToken = default)
    {
        var validationError = ValidateFile(path);
        if (validationError != null) return Task.FromResult(validationError);

        Regex regex;
        try
        {
            regex = new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        }
        catch (ArgumentException ex)
        {
            return Task.FromResult(JsonDefaults.Serialize(new { status = "error", message = $"Invalid regex pattern: {ex.Message}" }));
        }

        try
        {
            var (lines, encoding, _) = FileOutline.ReadAndDecodeFile(path);
            int totalLines = lines.Length;
            int matchedLines = 0;
            var groupDict = new Dictionary<string, LogGroup>(StringComparer.Ordinal);
            var groupsList = new List<LogGroup>();

            int clampedContextLines = Math.Clamp(contextLines, 0, 5);

            for (int i = 0; i < totalLines; i++)
            {
                int lineNum = i + 1;
                string line = lines[i];
                string cleanLine = FileOutline.StripAnsi(line).Trim();

                bool isMatch;
                try
                {
                    isMatch = regex.IsMatch(cleanLine);
                }
                catch (RegexMatchTimeoutException)
                {
                    return Task.FromResult(JsonDefaults.Serialize(new { status = "error", message = "Regex matching timed out." }));
                }

                if (!isMatch) continue;

                matchedLines++;
                string key = Regex.Replace(cleanLine, @"\d+", "#");

                if (groupDict.TryGetValue(key, out var existing))
                {
                    existing.Count++;
                    existing.LastLine = lineNum;
                }
                else
                {
                    var grp = new LogGroup
                    {
                        Count = 1,
                        FirstLine = lineNum,
                        LastLine = lineNum,
                        Text = cleanLine.Length <= 300 ? cleanLine : cleanLine[..300],
                        Context = []
                    };

                    if (clampedContextLines > 0)
                    {
                        int cStart = Math.Max(1, lineNum - clampedContextLines);
                        int cEnd = Math.Min(totalLines, lineNum + clampedContextLines);
                        for (int ci = cStart; ci <= cEnd; ci++)
                        {
                            string cLine = FileOutline.StripAnsi(lines[ci - 1]).Trim();
                            if (cLine.Length > 300) cLine = cLine[..300];
                            grp.Context.Add($"{ci}: {cLine}");
                        }
                    }

                    groupDict[key] = grp;
                    groupsList.Add(grp);
                }
            }

            int clampedMaxGroups = Math.Clamp(maxGroups, 1, 200);
            int groupCount = groupsList.Count;
            bool groupsTruncated = groupsList.Count > clampedMaxGroups;
            var groups = groupsList.Take(clampedMaxGroups).ToList();

            int clampedTailLines = Math.Clamp(tailLines, 0, 200);
            var tail = new List<string>();
            if (clampedTailLines > 0 && totalLines > 0)
            {
                int tailStart = Math.Max(1, totalLines - clampedTailLines + 1);
                for (int ti = tailStart; ti <= totalLines; ti++)
                {
                    string tLine = FileOutline.StripAnsi(lines[ti - 1]).Trim();
                    if (tLine.Length > 300) tLine = tLine[..300];
                    tail.Add($"{ti}: {tLine}");
                }
            }

            int clampedMaxChars = Math.Clamp(maxChars, 2000, 50000);
            bool truncated = false;

            string MakePayload(List<LogGroup> grps, bool grpTrunc, List<string> tl, bool trunc) =>
                JsonDefaults.Serialize(new
                {
                    status = "success",
                    path,
                    encoding,
                    totalLines,
                    matchedLines,
                    groupCount,
                    groups = grps,
                    groupsTruncated = grpTrunc,
                    tail = tl,
                    truncated = trunc
                });

            string json = MakePayload(groups, groupsTruncated, tail, truncated);
            if (json.Length > clampedMaxChars)
            {
                truncated = true;
                // Step 1: drop context from groups from the end
                for (int i = groups.Count - 1; i >= 0; i--)
                {
                    if (groups[i].Context.Count > 0)
                    {
                        groups[i].Context = [];
                        json = MakePayload(groups, groupsTruncated, tail, truncated);
                        if (json.Length <= clampedMaxChars) return Task.FromResult(json);
                    }
                }

                // Step 2: drop groups from the end
                while (groups.Count > 0)
                {
                    groups.RemoveAt(groups.Count - 1);
                    groupsTruncated = true;
                    json = MakePayload(groups, groupsTruncated, tail, truncated);
                    if (json.Length <= clampedMaxChars) return Task.FromResult(json);
                }

                // Step 3: shorten tail from the beginning until it fits
                while (tail.Count > 0)
                {
                    tail.RemoveAt(0);
                    json = MakePayload(groups, groupsTruncated, tail, truncated);
                    if (json.Length <= clampedMaxChars) return Task.FromResult(json);
                }
            }

            return Task.FromResult(json);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return Task.FromResult(JsonDefaults.Serialize(new { status = "error", message = ex.Message }));
        }
    }

    private static string? ValidateFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) || !File.Exists(path))
        {
            return JsonDefaults.Serialize(new { status = "error", message = "Path must be an existing absolute file path." });
        }

        try
        {
            var fileInfo = new FileInfo(path);
            if (fileInfo.Length > 50 * 1024 * 1024)
            {
                return JsonDefaults.Serialize(new { status = "error", message = "File exceeds 50 MB limit." });
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return JsonDefaults.Serialize(new { status = "error", message = ex.Message });
        }

        return null;
    }
}
