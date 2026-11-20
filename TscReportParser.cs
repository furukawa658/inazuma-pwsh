using System.Text.RegularExpressions;

namespace InazumaPwsh;

/// <summary>Parses tsc output produced with --pretty false.</summary>
internal static partial class TscReportParser
{
    private const int MaxDiagnostics = 15;
    private const int MaxMessageChars = 320;
    private const long MaxLogBytes = 20L * 1024 * 1024;

    [GeneratedRegex(@"^(?:(?<file>.+?)\((?<line>\d+),(?<col>\d+)\): )?error (?<code>TS\d+): (?<message>.*)$")]
    private static partial Regex DiagnosticRegex();

    private sealed record Diagnostic(string? File, int Line, int Column, string Code, string Message);

    /// <summary>Removes from raw compiler output the diagnostics that the report lists with their whole message.</summary>
    public static string WithoutListed(string output, System.Text.Json.Nodes.JsonObject result, string workingDirectory, IReadOnlySet<string>? previous = null, bool compact = true)
    {
        if (result["diagnostics"] is not System.Text.Json.Nodes.JsonArray array) return output;
        static string Key(string? file, int line, int column, string? code, string? message) => $"{file}|{line}|{column}|{code}|{message}";
        var listed = array.OfType<System.Text.Json.Nodes.JsonObject>().Select(item => Key(item["file"]?.GetValue<string>(),
            item["line"]?.GetValue<int>() ?? 0, item["column"]?.GetValue<int>() ?? 0, item["code"]?.GetValue<string>(), item["message"]?.GetValue<string>())).ToHashSet();
        var summarized = array.OfType<System.Text.Json.Nodes.JsonObject>()
            .Where(item => item["summary"] != null)
            .Select(item => $"{item["file"]?.GetValue<string>()}|{item["line"]?.GetValue<int>() ?? 0}|{item["column"]?.GetValue<int>() ?? 0}|{item["code"]?.GetValue<string>()}")
            .ToHashSet();

        var lines = output.Split('\n');
        var owner = new int[lines.Length];
        var found = new List<Diagnostic>();
        for (var index = 0; index < lines.Length; index++)
        {
            var text = FileOutline.StripAnsi(lines[index]).TrimEnd();
            var match = DiagnosticRegex().Match(text);
            owner[index] = -1;
            if (match.Success)
            {
                owner[index] = found.Count;
                found.Add(new Diagnostic(match.Groups["file"].Success ? match.Groups["file"].Value : null,
                    match.Groups["line"].Success ? int.Parse(match.Groups["line"].Value) : 0,
                    match.Groups["col"].Success ? int.Parse(match.Groups["col"].Value) : 0,
                    match.Groups["code"].Value, match.Groups["message"].Value));
            }
            else if (found.Count > 0 && text.Length > 0 && char.IsWhiteSpace(lines[index][0]))
            {
                owner[index] = found.Count - 1;
                found[^1] = found[^1] with { Message = found[^1].Message + " " + text.Trim() };
            }
        }
        // A truncated path or message does not equal the listed one, so its lines stay unless a compact summary identifies the diagnostic location and code.
        bool IsListed(Diagnostic d)
        {
            var file = d.File == null ? null : PlaywrightResultParser.ToRelativePath(d.File, workingDirectory);
            return listed.Contains(Key(file, d.Line, d.Column, d.Code, d.Message))
                || summarized.Contains($"{file}|{d.Line}|{d.Column}|{d.Code}");
        }
        var remaining = string.Join('\n', lines.Where((_, index) => owner[index] < 0 ||
            (!(previous?.Contains(BaselineKey(found[owner[index]])) ?? false) && (!compact || !IsListed(found[owner[index]])))));
        return string.IsNullOrWhiteSpace(remaining) ? "" : remaining;
    }

    /// <summary>When previous is given, only diagnostics absent from it are listed.</summary>
    public static string Parse(string logPath, string workingDirectory, int exitCode, IReadOnlySet<string>? previous, out HashSet<string>? current)
    {
        var diagnostics = new List<Diagnostic>();
        var truncatedLog = false;
        if (File.Exists(logPath))
        {
            truncatedLog = new FileInfo(logPath).Length > MaxLogBytes;
            if (!truncatedLog)
                foreach (var raw in File.ReadLines(logPath))
                {
                    var text = FileOutline.StripAnsi(raw).TrimEnd();
                    var match = DiagnosticRegex().Match(text);
                    if (match.Success)
                        diagnostics.Add(new Diagnostic(
                            match.Groups["file"].Success ? match.Groups["file"].Value : null,
                            match.Groups["line"].Success ? int.Parse(match.Groups["line"].Value) : 0,
                            match.Groups["col"].Success ? int.Parse(match.Groups["col"].Value) : 0,
                            match.Groups["code"].Value, match.Groups["message"].Value));
                    else if (diagnostics.Count > 0 && text.Length > 0 && char.IsWhiteSpace(raw[0]))
                        diagnostics[^1] = diagnostics[^1] with { Message = diagnostics[^1].Message + " " + text.Trim() };
                }
        }

        var counts = diagnostics.GroupBy(d => d.Code).OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal)
            .Take(10).ToDictionary(g => g.Key, g => g.Count());
        var status = diagnostics.Count > 0 ? "error"
            : truncatedLog || exitCode != 0 ? (exitCode == 0 ? "report_unavailable" : "error")
            : "success";
        // Line numbers are left out of the key because edits above a diagnostic shift them.
        var keys = truncatedLog ? null : diagnostics.Select(BaselineKey).ToHashSet();
        current = keys;
        var listed = previous == null ? diagnostics : diagnostics.Where(d => !previous.Contains(BaselineKey(d))).ToList();
        var shown = listed.Take(MaxDiagnostics).Select(d => new
        {
            file = d.File == null ? null : PlaywrightResultParser.Truncate(PlaywrightResultParser.ToRelativePath(d.File, workingDirectory), 300),
            line = d.Line == 0 ? (int?)null : d.Line,
            column = d.Column == 0 ? (int?)null : d.Column,
            code = d.Code,
            message = PlaywrightResultParser.Truncate(d.Message, MaxMessageChars)
        }).ToList();
        var report = System.Text.Json.JsonSerializer.Serialize(new
        {
            status,
            exitCode,
            errorCount = diagnostics.Count,
            errorCodeCounts = counts,
            newCount = previous == null ? (int?)null : listed.Count,
            fixedCount = previous == null || keys == null ? (int?)null : previous.Count(key => !keys.Contains(key)),
            diagnostics = shown,
            omitted = Math.Max(0, listed.Count - MaxDiagnostics)
        }, JsonDefaults.OptionsIgnoreNull);
        return DiagnosticBudget.Fit(report, 5500, "diagnostics");
    }

    private static string BaselineKey(Diagnostic diagnostic) => $"{diagnostic.File}|{diagnostic.Code}|{diagnostic.Message}";
}
