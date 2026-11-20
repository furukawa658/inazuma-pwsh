using System.Text.Json;

namespace InazumaPwsh;

internal static class CargoCheckParser
{
    private const int MaxDiagnostics = 15;
    private const long MaxLogBytes = 50L * 1024 * 1024;

    private sealed record Diagnostic(string File, int Line, int Column, string Code, string Level, string Message);

    public static string Parse(string logPath, string workingDirectory, int exitCode, IReadOnlySet<string>? previous,
        out HashSet<string>? current)
    {
        var diagnostics = new List<Diagnostic>();
        var truncatedLog = false;
        var validMessage = false;
        if (File.Exists(logPath))
        {
            truncatedLog = new FileInfo(logPath).Length > MaxLogBytes;
            if (!truncatedLog)
            {
                foreach (var raw in File.ReadLines(logPath))
                {
                    var line = raw.Trim();
                    if (!line.StartsWith('{') || !line.EndsWith('}')) continue;
                    try
                    {
                        using var document = JsonDocument.Parse(line);
                        var root = document.RootElement;
                        if (!Text(root, "reason").Equals("compiler-message", StringComparison.Ordinal)) continue;
                        if (!root.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object) continue;
                        var level = Text(message, "level");
                        if (level is not ("error" or "warning")) continue;
                        validMessage = true;
                        var code = message.TryGetProperty("code", out var codeNode) && codeNode.ValueKind == JsonValueKind.Object
                            ? Text(codeNode, "code") : "";
                        var text = Text(message, "message");
                        var (file, row, column) = PrimarySpan(message);
                        if (file.Length > 0) file = PlaywrightResultParser.ToRelativePath(file, workingDirectory);
                        diagnostics.Add(new Diagnostic(PlaywrightResultParser.Truncate(file, 300), row, column,
                            code, level, PlaywrightResultParser.Truncate(text, 500)));
                    }
                    catch (JsonException) { }
                }
            }
        }

        var unique = diagnostics
            .GroupBy(diagnostic => $"{diagnostic.Level}|{diagnostic.File}|{diagnostic.Line}|{diagnostic.Column}|{diagnostic.Code}|{diagnostic.Message}", StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();
        current = !truncatedLog && (exitCode == 0 || validMessage)
            ? unique.Select(BaselineKey).ToHashSet(StringComparer.OrdinalIgnoreCase)
            : null;
        var currentKeys = current;
        var listed = previous == null ? unique : unique.Where(diagnostic => !previous.Contains(BaselineKey(diagnostic))).ToList();
        var ordered = listed.OrderByDescending(diagnostic => diagnostic.Level == "error").ThenBy(diagnostic => diagnostic.File, StringComparer.Ordinal).ThenBy(diagnostic => diagnostic.Line).ToList();
        var errors = unique.Count(diagnostic => diagnostic.Level == "error");
        var warnings = unique.Count(diagnostic => diagnostic.Level == "warning");
        var status = exitCode != 0 ? "error" : "success";
        if (truncatedLog && exitCode == 0) status = "report_unavailable";
        var report = JsonSerializer.Serialize(new
        {
            status,
            exitCode,
            errorCount = errors,
            warningCount = warnings,
            diagnostics = ordered.Take(MaxDiagnostics).Select(diagnostic => new
            {
                file = diagnostic.File,
                line = diagnostic.Line == 0 ? (int?)null : diagnostic.Line,
                column = diagnostic.Column == 0 ? (int?)null : diagnostic.Column,
                code = diagnostic.Code.Length == 0 ? null : diagnostic.Code,
                severity = diagnostic.Level,
                message = diagnostic.Message
            }).ToList(),
            omitted = Math.Max(0, ordered.Count - MaxDiagnostics),
            newCount = previous == null || currentKeys == null ? (int?)null : currentKeys.Count(key => !previous.Contains(key)),
            fixedCount = previous == null || currentKeys == null ? (int?)null : previous.Count(key => !currentKeys.Contains(key))
        }, JsonDefaults.OptionsIgnoreNull);
        return DiagnosticBudget.Fit(report, 5500, "diagnostics");
    }

    public static string WithoutCompilerMessages(string output)
    {
        var lines = output.Replace("\r\n", "\n").Split('\n');
        return string.Join('\n', lines.Where(line =>
        {
            var text = line.Trim();
            if (!text.StartsWith('{') || !text.EndsWith('}')) return true;
            try
            {
                using var document = JsonDocument.Parse(text);
                return !Text(document.RootElement, "reason").Equals("compiler-message", StringComparison.Ordinal);
            }
            catch (JsonException) { return true; }
        })).Trim('\n');
    }

    private static (string File, int Line, int Column) PrimarySpan(JsonElement message)
    {
        if (!message.TryGetProperty("spans", out var spans) || spans.ValueKind != JsonValueKind.Array) return ("", 0, 0);
        JsonElement? fallback = null;
        foreach (var span in spans.EnumerateArray())
        {
            if (span.ValueKind != JsonValueKind.Object) continue;
            fallback ??= span;
            if (span.TryGetProperty("is_primary", out var primary) && primary.ValueKind == JsonValueKind.True)
                return Span(span);
        }
        return fallback is { } value ? Span(value) : ("", 0, 0);
    }

    private static (string File, int Line, int Column) Span(JsonElement span) =>
        (Text(span, "file_name"), Number(span, "line_start"), Number(span, "column_start"));

    private static string BaselineKey(Diagnostic diagnostic) => $"{diagnostic.Level}|{diagnostic.File}|{diagnostic.Code}|{diagnostic.Message}";

    private static string Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";

    private static int Number(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) ? number : 0;
}
