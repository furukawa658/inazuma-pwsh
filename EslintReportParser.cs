using System.Text.Json;

namespace InazumaPwsh;

/// <summary>Parses the report written by the ESLint json formatter.</summary>
internal static class EslintReportParser
{
    private const int MaxDiagnostics = 15;
    private const long MaxReportBytes = 50L * 1024 * 1024;

    private sealed record Diagnostic(string File, int Line, int Column, string? Rule, bool IsError, string Message);

    public static async Task<string> ParseAsync(string reportPath, string workingDirectory, int exitCode, CancellationToken cancellationToken)
    {
        var diagnostics = new List<Diagnostic>();
        var valid = false;
        try
        {
            if (File.Exists(reportPath) && new FileInfo(reportPath).Length <= MaxReportBytes)
            {
                await using var stream = new FileStream(reportPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
                if (document.RootElement.ValueKind == JsonValueKind.Array)
                {
                    valid = true;
                    foreach (var file in document.RootElement.EnumerateArray())
                    {
                        if (file.ValueKind != JsonValueKind.Object || !file.TryGetProperty("messages", out var messages) || messages.ValueKind != JsonValueKind.Array) continue;
                        var path = PlaywrightResultParser.ToRelativePath(Text(file, "filePath"), workingDirectory);
                        foreach (var message in messages.EnumerateArray())
                        {
                            if (message.ValueKind != JsonValueKind.Object) continue;
                            var rule = Text(message, "ruleId");
                            diagnostics.Add(new Diagnostic(path, Number(message, "line"), Number(message, "column"),
                                rule.Length == 0 ? null : rule, Number(message, "severity") >= 2, Text(message, "message")));
                        }
                    }
                }
            }
        }
        catch (Exception exception) when (exception is JsonException or IOException or InvalidOperationException)
        {
            valid = false;
        }

        var errorCount = diagnostics.Count(d => d.IsError);
        var status = !valid ? (exitCode == 0 ? "report_unavailable" : "error") : exitCode == 0 && errorCount == 0 ? "success" : "error";
        var listed = diagnostics.OrderByDescending(d => d.IsError).ToList();
        return JsonSerializer.Serialize(new
        {
            status,
            exitCode,
            errorCount,
            warningCount = diagnostics.Count - errorCount,
            ruleCounts = diagnostics.Where(d => d.Rule != null).GroupBy(d => d.Rule!).OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal)
                .Take(10).ToDictionary(g => g.Key, g => g.Count()),
            diagnostics = listed.Take(MaxDiagnostics).Select(d => new
            {
                file = PlaywrightResultParser.Truncate(d.File, 300),
                line = d.Line == 0 ? (int?)null : d.Line,
                column = d.Column == 0 ? (int?)null : d.Column,
                rule = d.Rule,
                severity = d.IsError ? "error" : "warning",
                message = PlaywrightResultParser.Truncate(d.Message, 300)
            }).ToList(),
            omitted = Math.Max(0, listed.Count - MaxDiagnostics)
        }, JsonDefaults.OptionsIgnoreNull);
    }

    private static string Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";

    private static int Number(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) ? number : 0;
}
