using System.ComponentModel;
using System.Text;
using System.Text.Json;
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
    [Description("Return a compact outline of a text file: line count, kind, and line ranges of headings, declarations or top-level keys. Each entry is \"start-end level title\". Use it before reading a large file, then read only the needed range with read_section. If recommend is FULL the file is small enough to read entirely.")]
    public Task<string> PeekFileAsync(
        [Description("Absolute path to the text file.")] string path,
        [Description("Maximum line count for recommending full read (1 to 5000, default 200).")] int fullThreshold = 200,
        [Description("Maximum number of outline entries to return (10 to 1000, default 200).")] int maxEntries = 200,
        [Description("Optional maximum outline level to include.")] int? maxLevel = null,
        CancellationToken cancellationToken = default) =>
        UsageLog.TrackFileAsync("peek_file", path, PeekFile(path, fullThreshold, maxEntries, maxLevel));

    private static Task<string> PeekFile(string path, int fullThreshold, int maxEntries, int? maxLevel)
    {
        var validationError = ValidateFile(path);
        if (validationError != null) return Task.FromResult(validationError);

        try
        {
            var (lines, encoding, bytes) = FileOutline.ReadAndDecodeFile(path);
            string kind = FileOutline.DetermineKind(path);
            var allEntries = FileOutline.GetOutline(path, lines, kind);
            if (maxLevel.HasValue) allEntries = allEntries.Where(entry => entry.Level <= maxLevel.Value).ToList();

            int clampedThreshold = Math.Clamp(fullThreshold, 1, 5000);
            int clampedMaxEntries = Math.Clamp(maxEntries, 10, 1000);

            string recommend = lines.Length <= clampedThreshold ? "FULL" : "PARTIAL";
            bool entriesTruncated = allEntries.Count > clampedMaxEntries;
            var entries = allEntries.Take(clampedMaxEntries).Select(entry => $"{entry.Start}-{entry.End} {entry.Level} {entry.Title}").ToList();

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
        [Description("Optional array of section names or objects with query or startLine and endLine; up to 10 ranges.")] JsonElement[]? ranges = null,
        CancellationToken cancellationToken = default) =>
        UsageLog.TrackFileAsync("read_section", path, ReadSections(path, query, startLine, endLine, maxChars, ranges));

    private static async Task<string> ReadSections(string path, string? query, int? startLine, int? endLine, int maxChars, JsonElement[]? ranges)
    {
        if (ranges == null) return await ReadSection(path, query, startLine, endLine, maxChars);
        if (ranges.Length is < 1 or > 10 || query != null || startLine.HasValue || endLine.HasValue)
            return JsonDefaults.Serialize(new { status = "error", message = "Specify 1 to 10 ranges without query, startLine or endLine." });

        var results = new List<object>();
        foreach (var range in ranges)
        {
            string? section = null;
            int? start = null, end = null;
            if (range.ValueKind == JsonValueKind.String) section = range.GetString();
            else if (range.ValueKind == JsonValueKind.Object)
            {
                if (range.TryGetProperty("query", out var queryValue) && queryValue.ValueKind == JsonValueKind.String) section = queryValue.GetString();
                if (range.TryGetProperty("startLine", out var startValue) && startValue.ValueKind == JsonValueKind.Number && startValue.TryGetInt32(out var startNumber)) start = startNumber;
                if (range.TryGetProperty("endLine", out var endValue) && endValue.ValueKind == JsonValueKind.Number && endValue.TryGetInt32(out var endNumber)) end = endNumber;
            }
            else return JsonDefaults.Serialize(new { status = "error", message = "Each range must be a section name or an object." });

            string label = section ?? (start.HasValue ? $"{start}-{(end.HasValue ? end.ToString() : "")}" : "invalid range");
            string result = await ReadSection(path, section, start, end, maxChars);
            using var document = JsonDocument.Parse(result);
            results.Add(new { label, result = document.RootElement.Clone() });
        }
        return JsonDefaults.Serialize(new { status = "success", path, ranges = results });
    }

    private static Task<string> ReadSection(string path, string? query, int? startLine, int? endLine, int maxChars)
    {
        var validationError = ValidateFile(path);
        if (validationError != null) return Task.FromResult(validationError);

        bool hasQuery = !string.IsNullOrEmpty(query);
        bool hasStart = startLine.HasValue;

        if (hasStart && endLine.HasValue && startLine > endLine)
            return Task.FromResult(JsonDefaults.Serialize(new { status = "error", message = "startLine must not be greater than endLine." }));

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
                    var candidates = allEntries
                        .Select(entry => new { entry.Title, Score = SimilarityScore(query!, entry.Title) })
                        .Where(entry => entry.Score <= Math.Max(2, query!.Length / 3))
                        .OrderBy(entry => entry.Score)
                        .Take(10).Select(entry => entry.Title).ToArray();
                    var bodyMatches = candidates.Length == 0
                        ? lines.Select((line, index) => new { line = index + 1, text = line })
                            .Where(item => item.text.Contains(query!, StringComparison.OrdinalIgnoreCase)).Take(10).ToArray()
                        : [];
                    return Task.FromResult(JsonDefaults.Serialize(new
                    {
                        status = "error",
                        message = "No outline entry matched.",
                        candidates,
                        bodyMatches
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

    private static int SimilarityScore(string query, string title)
    {
        if (query.Length > 256) return int.MaxValue;
        query = query.ToUpperInvariant();
        title = title.ToUpperInvariant();
        if (query.Contains(title, StringComparison.Ordinal)) return 0;
        var words = Regex.Split(title, @"[^\p{L}\p{Nd}_]+").Where(word => word.Length > 0);
        int best = EditDistance(query, title);
        foreach (string word in words) best = Math.Min(best, EditDistance(query, word));
        return best;
    }

    private static int EditDistance(string left, string right)
    {
        var previous = Enumerable.Range(0, right.Length + 1).ToArray();
        for (int row = 1; row <= left.Length; row++)
        {
            var current = new int[right.Length + 1];
            current[0] = row;
            for (int column = 1; column <= right.Length; column++)
                current[column] = Math.Min(Math.Min(current[column - 1] + 1, previous[column] + 1), previous[column - 1] + (left[row - 1] == right[column - 1] ? 0 : 1));
            previous = current;
        }
        return previous[right.Length];
    }

    [McpServerTool(Name = "summarize_log")]
    [Description("Summarize a large log without reading all of it: lines matching pattern (case-insensitive regex) grouped by text with digits normalized, with occurrence counts, first and last line numbers and the lines around the first occurrence (context; the matched line itself is only in text), plus the last lines of the file. Use it for build logs and fullLogPath files.")]
    public Task<string> SummarizeLogAsync(
        [Description("Absolute path to the log file.")] string path,
        [Description("Regex pattern to match error lines (default includes English and Japanese errors and warnings).")] string pattern = "error|fail|exception|fatal|warn|エラー|警告|失敗|例外",
        [Description("Context lines before and after the first occurrence (clamped 0..5, default 1).")] int contextLines = 1,
        [Description("Number of tail lines from the end of the file (clamped 0..200, default 20).")] int tailLines = 20,
        [Description("Maximum number of groups to return (clamped 1..200, default 30).")] int maxGroups = 30,
        [Description("Maximum characters of serialized output JSON (clamped 2000..50000, default 8000).")] int maxChars = 8000,
        CancellationToken cancellationToken = default) =>
        UsageLog.TrackFileAsync("summarize_log", path, SummarizeLog(path, pattern, contextLines, tailLines, maxGroups, maxChars));

    private static Task<string> SummarizeLog(string path, string pattern, int contextLines, int tailLines, int maxGroups, int maxChars)
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
                    string searchableLine = pattern == "error|fail|exception|fatal|warn|エラー|警告|失敗|例外"
                        ? Regex.Replace(cleanLine, @"\b0\s*(?:(?:件|個)\s*)?(?:の\s*)?(?:errors?(?:\(s\))?|warnings?(?:\(s\))?|エラー|警告|失敗|例外)", "", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
                        : cleanLine;
                    isMatch = regex.IsMatch(searchableLine);
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
                            if (ci == lineNum) continue;
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

    private static readonly string[] SkippedDirectories = [".git", "node_modules", ".venv", "bin", "obj"];

    private sealed record SearchHit(string Text, int Line, string[] Before, string[] After);
    private sealed record FileMatches(string File, List<SearchHit> Hits) { public int Count { get; set; } public int Shown { get; set; } }

    [McpServerTool(Name = "search_files")]
    [Description("Search text files using a regex or literal pattern. Respects Git ignore rules and skips .git, node_modules, .venv, bin, obj, binary files and files over 5 MB.")]
    public Task<string> SearchFilesAsync(
        [Description("Absolute path to a directory or file.")] string path,
        [Description("Regex pattern, case-insensitive unless caseSensitive is true.")] string pattern,
        [Description("Optional file-name wildcard such as *.cs or *.test.ts.")] string? fileName = null,
        [Description("Maximum matching lines returned across all files (clamped 1..100, default 30).")] int maxResults = 30,
        bool caseSensitive = false,
        [Description("Maximum displayed matches per file (1..100, default 20).")] int maxPerFile = 20,
        [Description("Treat pattern as literal text instead of regex.")] bool literal = false,
        [Description("Context lines before and after each match (0..5).")] int contextLines = 0,
        [Description("Glob patterns to exclude, relative to the search path.")] string[]? exclude = null,
        CancellationToken cancellationToken = default)
    {
        return UsageLog.TrackCallAsync("search_files", SearchFilesCoreAsync(path, pattern, fileName, maxResults, caseSensitive, maxPerFile, literal, contextLines, exclude, cancellationToken));
    }

    private static Task<string> SearchFilesCoreAsync(string path, string pattern, string? fileName, int maxResults,
        bool caseSensitive, int maxPerFile, bool literal, int contextLines, string[]? exclude, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) || (!Directory.Exists(path) && !File.Exists(path)))
            return Task.FromResult(JsonDefaults.Serialize(new { status = "error", message = "Path must be an existing absolute directory or file path." }));
        Regex regex;
        try
        {
            regex = new Regex(literal ? Regex.Escape(pattern) : pattern, (caseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase) | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        }
        catch (ArgumentException ex)
        {
            return Task.FromResult(JsonDefaults.Serialize(new { status = "error", message = $"Invalid regex pattern: {ex.Message}" }));
        }
        return Task.Run(() => SearchFiles(path, regex, fileName, Math.Clamp(maxResults, 1, 100), Math.Clamp(maxPerFile, 1, 100), Math.Clamp(contextLines, 0, 5), exclude, cancellationToken), cancellationToken);
    }

    private static string SearchFiles(string path, Regex regex, string? fileName, int maxResults, int maxPerFile, int contextLines, string[]? exclude, CancellationToken cancellationToken)
    {
        const int MaxFiles = 50000, MaxListedFiles = 40, MaxLineChars = 200;
        var root = Directory.Exists(path) ? path : Path.GetDirectoryName(path)!;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var found = new List<FileMatches>();
        int filesScanned = 0, filesMatched = 0, totalMatches = 0, shown = 0;
        var stoppedEarly = false;
        try
        {
            var files = EnumerateSearchFiles(path, fileName).Take(MaxFiles + 1).ToArray();
            if (files.Length > MaxFiles) { stoppedEarly = true; files = files.Take(MaxFiles).ToArray(); }
            var ignored = GitIgnoredFiles(root, files);
            foreach (var file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (filesScanned >= MaxFiles || watch.Elapsed > TimeSpan.FromSeconds(20)) { stoppedEarly = true; break; }
                string relative = Path.GetRelativePath(root, file).Replace('\\', '/');
                if (ignored.Contains(file) || (exclude != null && exclude.Any(glob => GlobMatches(glob, relative)))) continue;
                string[] lines;
                try
                {
                    var info = new FileInfo(file);
                    if (info.Length > 5 * 1024 * 1024 || IsBinary(file)) continue;
                    lines = FileOutline.ReadAndDecodeFile(file).Lines;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
                filesScanned++;

                FileMatches? current = null;
                var listed = false;
                for (var index = 0; index < lines.Length; index++)
                {
                    if (!regex.IsMatch(lines[index])) continue;
                    totalMatches++;
                    if (current == null)
                    {
                        filesMatched++;
                        current = new FileMatches(relative, []);
                        listed = found.Count < MaxListedFiles;
                        if (listed) found.Add(current);
                    }
                    current.Count++;
                    if (!listed || current.Hits.Count >= maxPerFile) continue;
                    string FormatLine(int lineIndex)
                    {
                        var text = lines[lineIndex].TrimEnd();
                        return $"{lineIndex + 1}| {(text.Length > MaxLineChars ? text[..MaxLineChars] + "..." : text)}";
                    }
                    var before = Enumerable.Range(Math.Max(0, index - contextLines), Math.Min(index, contextLines)).Select(FormatLine).ToArray();
                    var after = Enumerable.Range(index + 1, Math.Min(contextLines, lines.Length - index - 1)).Select(FormatLine).ToArray();
                    current.Hits.Add(new SearchHit(FormatLine(index), index + 1, before, after));
                }
            }
        }
        catch (RegexMatchTimeoutException)
        {
            return JsonDefaults.Serialize(new { status = "error", message = "Regex matching timed out." });
        }
        catch (OperationCanceledException)
        {
            return JsonDefaults.Serialize(new { status = "cancelled" });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            return JsonDefaults.Serialize(new { status = "error", message = ex.Message });
        }

        while (shown < maxResults)
        {
            bool added = false;
            foreach (var item in found)
            {
                if (item.Shown >= item.Hits.Count || shown >= maxResults) continue;
                item.Shown++;
                shown++;
                added = true;
            }
            if (!added) break;
        }

        string Serialize() => System.Text.Json.JsonSerializer.Serialize(new
        {
            status = "success",
            filesScanned,
            filesMatched,
            totalMatches,
            files = found.Select(item => new { file = item.File, count = item.Count, matches = item.Shown == 0 ? null : item.Hits.Take(item.Shown).Select(hit => hit.Text),
                omitted = item.Count > item.Shown ? item.Count - item.Shown : (int?)null, context = contextLines == 0 || item.Shown == 0 ? null : item.Hits.Take(item.Shown).Select(hit => new { line = hit.Line, before = hit.Before, after = hit.After }) }),
            truncated = shown < totalMatches || filesMatched > found.Count ? true : (bool?)null,
            stoppedEarly = stoppedEarly ? true : (bool?)null
        }, JsonDefaults.OptionsIgnoreNull);

        var json = Serialize();
        for (var index = found.Count - 1; index >= 0 && json.Length > 8000; index--)
        {
            shown -= found[index].Shown;
            found[index].Shown = 0;
            json = Serialize();
        }
        return json;
    }

    private static bool GlobMatches(string glob, string relative)
    {
        glob = glob.Replace('\\', '/');
        string pattern = "^" + Regex.Escape(glob).Replace(@"\*\*/", "(?:.*/)?").Replace(@"\*\*", ".*").Replace(@"\*", "[^/]*").Replace(@"\?", "[^/]") + "$";
        return Regex.IsMatch(glob.Contains('/') ? relative : Path.GetFileName(relative), pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    }

    private static HashSet<string> GitIgnoredFiles(string root, string[] files)
    {
        var ignored = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        var directory = new DirectoryInfo(root);
        while (directory != null && !Directory.Exists(Path.Combine(directory.FullName, ".git")) && !File.Exists(Path.Combine(directory.FullName, ".git"))) directory = directory.Parent;
        if (directory == null || files.Length == 0) return ignored;
        var startInfo = new System.Diagnostics.ProcessStartInfo("git") { WorkingDirectory = directory.FullName, UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        startInfo.ArgumentList.Add("check-ignore");
        startInfo.ArgumentList.Add("-z");
        startInfo.ArgumentList.Add("--stdin");
        using var process = System.Diagnostics.Process.Start(startInfo) ?? throw new IOException("Could not start git check-ignore.");
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        foreach (var file in files) { process.StandardInput.Write(file); process.StandardInput.Write('\0'); }
        process.StandardInput.Close();
        if (!process.WaitForExit(20000)) { process.Kill(true); throw new IOException("git check-ignore timed out."); }
        if (process.ExitCode is not (0 or 1)) throw new IOException($"git check-ignore failed: {errors.GetAwaiter().GetResult()}");
        foreach (var file in output.GetAwaiter().GetResult().Split('\0', StringSplitOptions.RemoveEmptyEntries)) ignored.Add(file);
        return ignored;
    }

    private static IEnumerable<string> EnumerateSearchFiles(string path, string? fileName)
    {
        if (File.Exists(path)) { yield return path; yield break; }
        var pending = new Stack<string>();
        pending.Push(path);
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            string[] files, directories;
            try
            {
                files = Directory.GetFiles(directory);
                directories = Directory.GetDirectories(directory);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            Array.Sort(files, StringComparer.Ordinal);
            foreach (var file in files)
                if (string.IsNullOrEmpty(fileName) || System.IO.Enumeration.FileSystemName.MatchesSimpleExpression(fileName, Path.GetFileName(file)))
                    yield return file;
            Array.Sort(directories, StringComparer.Ordinal);
            foreach (var child in directories.Reverse())
            {
                var info = new DirectoryInfo(child);
                if (SkippedDirectories.Contains(info.Name, StringComparer.OrdinalIgnoreCase) || info.Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
                pending.Push(child);
            }
        }
    }

    private static bool IsBinary(string path)
    {
        var buffer = new byte[4096];
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var count = stream.Read(buffer, 0, buffer.Length);
        var utf16 = count >= 2 && ((buffer[0] == 0xFF && buffer[1] == 0xFE) || (buffer[0] == 0xFE && buffer[1] == 0xFF));
        return !utf16 && Array.IndexOf(buffer, (byte)0, 0, count) >= 0;
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
