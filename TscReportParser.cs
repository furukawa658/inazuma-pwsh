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

    public static string Parse(string logPath, string workingDirectory, int exitCode)
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
        var shown = diagnostics.Take(MaxDiagnostics).Select(d => new
        {
            file = d.File == null ? null : PlaywrightResultParser.Truncate(PlaywrightResultParser.ToRelativePath(d.File, workingDirectory), 300),
            line = d.Line == 0 ? (int?)null : d.Line,
            column = d.Column == 0 ? (int?)null : d.Column,
            code = d.Code,
            message = PlaywrightResultParser.Truncate(d.Message, MaxMessageChars)
        }).ToList();
        return System.Text.Json.JsonSerializer.Serialize(new
        {
            status,
            exitCode,
            errorCount = diagnostics.Count,
            errorCodeCounts = counts,
            diagnostics = shown,
            omitted = Math.Max(0, diagnostics.Count - MaxDiagnostics)
        }, JsonDefaults.OptionsIgnoreNull);
    }
}
