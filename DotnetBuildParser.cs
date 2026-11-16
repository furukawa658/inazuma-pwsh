using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace InazumaPwsh;

public static class DotnetBuildParser
{
    private record Diagnostic(
        [property: JsonPropertyName("ruleId")] string RuleId,
        [property: JsonPropertyName("level")] string Level,
        [property: JsonPropertyName("message")] string Message,
        [property: JsonPropertyName("path")] string Path,
        [property: JsonPropertyName("line")] int Line,
        [property: JsonPropertyName("column")] int Column
    );

    private record Summary(
        [property: JsonPropertyName("totalErrors")] int TotalErrors,
        [property: JsonPropertyName("totalWarnings")] int TotalWarnings,
        [property: JsonPropertyName("capturedErrors")] int CapturedErrors,
        [property: JsonPropertyName("capturedWarnings")] int CapturedWarnings,
        [property: JsonPropertyName("filesEvaluated")] int FilesEvaluated
    );

    private record BuildReport(
        [property: JsonPropertyName("status")] string Status,
        [property: JsonPropertyName("exitCode")] int ExitCode,
        [property: JsonPropertyName("summary")] Summary Summary,
        [property: JsonPropertyName("errors")] List<Diagnostic> Errors,
        [property: JsonPropertyName("warnings")] List<Diagnostic> Warnings
    );

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static async Task<string> ParseAsync(
        string reportPath,
        string workingDirectory,
        int exitCode,
        bool includeWarnings = false,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var sarifFiles = new List<string>();
        if (!string.IsNullOrWhiteSpace(reportPath))
        {
            if (File.Exists(reportPath))
            {
                sarifFiles.Add(reportPath);
            }
            else if (Directory.Exists(reportPath))
            {
                try
                {
                    sarifFiles.AddRange(Directory.EnumerateFiles(reportPath, "*.sarif", SearchOption.AllDirectories).Take(200));
                }
                catch { }
            }
        }

        if (sarifFiles.Count == 0)
        {
            var emptyStatus = exitCode == 0 ? "report_unavailable" : "error";
            return JsonSerializer.Serialize(new BuildReport(
                emptyStatus, exitCode, new Summary(0, 0, 0, 0, 0), [], []),
                JsonOptions);
        }

        var allErrors = new List<Diagnostic>();
        var allWarnings = new List<Diagnostic>();
        var seenKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int totalErrors = 0, totalWarnings = 0, filesEvaluated = 0;
        bool invalidReport = false;

        foreach (var file in sarifFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (new FileInfo(file).Length > 50 * 1024 * 1024)
                {
                    invalidReport = true;
                    continue;
                }
                await using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

                if (doc.RootElement.ValueKind != JsonValueKind.Object)
                {
                    invalidReport = true;
                    continue;
                }

                bool hasSarifMeta = doc.RootElement.TryGetProperty("version", out _) || doc.RootElement.TryGetProperty("$schema", out _);
                if (!hasSarifMeta || !doc.RootElement.TryGetProperty("runs", out var runs) || runs.ValueKind != JsonValueKind.Array)
                {
                    invalidReport = true;
                    continue;
                }

                bool hasValidRun = false;
                foreach (var run in runs.EnumerateArray())
                {
                    if (run.ValueKind != JsonValueKind.Object)
                    {
                        invalidReport = true;
                        continue;
                    }

                    if (!run.TryGetProperty("tool", out var tool) || tool.ValueKind != JsonValueKind.Object ||
                        !run.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array)
                    {
                        invalidReport = true;
                        continue;
                    }

                    hasValidRun = true;

                    foreach (var res in results.EnumerateArray())
                    {
                        if (res.ValueKind != JsonValueKind.Object) continue;
                        var ruleId = Truncate(res.TryGetProperty("ruleId", out var rId) ? rId.GetString() ?? "" : "", 100);
                        var level = res.TryGetProperty("level", out var lvl) ? lvl.GetString()?.ToLowerInvariant() ?? "warning" : "warning";
                        if (string.IsNullOrEmpty(level)) level = "warning";
                        bool isError = level == "error";

                        var msg = "";
                        if (res.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.Object && m.TryGetProperty("text", out var mt))
                            msg = Truncate(mt.GetString() ?? "", 500);

                        var (rawPath, line, col) = ExtractLocation(res, workingDirectory);
                        var path = Truncate(rawPath, 300);
                        var dedupeKey = $"{ruleId}|{path}|{line}|{col}|{msg}";
                        if (!seenKeys.Add(dedupeKey)) continue;

                        if (isError)
                        {
                            totalErrors++;
                            if (allErrors.Count < 30)
                                allErrors.Add(new Diagnostic(ruleId, level, msg, path, line, col));
                        }
                        else
                        {
                            totalWarnings++;
                            if (includeWarnings && allWarnings.Count < 15)
                                allWarnings.Add(new Diagnostic(ruleId, level, msg, path, line, col));
                        }
                    }
                }

                if (!hasValidRun)
                {
                    invalidReport = true;
                }
                else
                {
                    filesEvaluated++;
                }
            }
            catch (Exception exception) when (exception is JsonException or IOException or InvalidOperationException or FormatException or OverflowException)
            {
                invalidReport = true;
            }
        }

        string status = filesEvaluated == 0 || invalidReport
            ? (exitCode == 0 ? "report_unavailable" : "error")
            : (exitCode == 0 && totalErrors == 0 ? "success" : "error");

        return SerializeWithLimit(status, exitCode, totalErrors, totalWarnings, filesEvaluated, allErrors, allWarnings);
    }

    private static (string path, int line, int col) ExtractLocation(JsonElement res, string workingDir)
    {
        if (res.TryGetProperty("locations", out var locs) && locs.ValueKind == JsonValueKind.Array)
        {
            foreach (var loc in locs.EnumerateArray())
            {
                if (loc.TryGetProperty("physicalLocation", out var phys))
                {
                    var uri = "";
                    if (phys.TryGetProperty("artifactLocation", out var art) && art.TryGetProperty("uri", out var u))
                        uri = u.GetString() ?? "";

                    int line = 0, col = 0;
                    if (phys.TryGetProperty("region", out var reg))
                    {
                        if (reg.TryGetProperty("startLine", out var sl)) line = sl.GetInt32();
                        if (reg.TryGetProperty("startColumn", out var sc)) col = sc.GetInt32();
                    }

                    return (NormalizePath(uri, workingDir), line, col);
                }
            }
        }
        return ("", 0, 0);
    }

    private static string NormalizePath(string uri, string workingDir)
    {
        if (string.IsNullOrWhiteSpace(uri)) return "";
        try
        {
            if (Uri.TryCreate(uri, UriKind.Absolute, out var parsed) && parsed.IsFile)
            {
                var local = parsed.LocalPath;
                return !string.IsNullOrEmpty(workingDir) ? Path.GetRelativePath(workingDir, local).Replace('\\', '/') : local.Replace('\\', '/');
            }
            if (Path.IsPathRooted(uri) && !string.IsNullOrEmpty(workingDir))
            {
                return Path.GetRelativePath(workingDir, uri).Replace('\\', '/');
            }
            return uri.Replace('\\', '/');
        }
        catch { return uri.Replace('\\', '/'); }
    }

    private static string SerializeWithLimit(
        string status, int exitCode, int totalErrors, int totalWarnings, int filesEvaluated,
        List<Diagnostic> errors, List<Diagnostic> warnings)
    {
        var curErrors = errors;
        var curWarnings = warnings;

        for (int attempt = 0; attempt < 4; attempt++)
        {
            var summary = new Summary(totalErrors, totalWarnings, curErrors.Count, curWarnings.Count, filesEvaluated);
            var report = new BuildReport(status, exitCode, summary, curErrors, curWarnings);
            var json = JsonSerializer.Serialize(report, JsonOptions);
            if (json.Length <= 5500) return json;

            if (attempt == 0)
            {
                curErrors = curErrors.Select(e => e with { Message = Truncate(e.Message, 160), Path = Truncate(e.Path, 150) }).ToList();
                curWarnings = curWarnings.Select(w => w with { Message = Truncate(w.Message, 160), Path = Truncate(w.Path, 150) }).ToList();
            }
            else if (attempt == 1)
            {
                curWarnings = curWarnings.Take(5).Select(w => w with { Message = Truncate(w.Message, 100), Path = Truncate(w.Path, 100) }).ToList();
                curErrors = curErrors.Take(15).Select(e => e with { Message = Truncate(e.Message, 100), Path = Truncate(e.Path, 100) }).ToList();
            }
            else if (attempt == 2)
            {
                curWarnings = [];
                curErrors = curErrors.Take(5).Select(e => e with { Message = Truncate(e.Message, 80), Path = Truncate(e.Path, 80) }).ToList();
            }
            else
            {
                curWarnings = [];
                curErrors = curErrors.Take(1).Select(e => e with { Message = Truncate(e.Message, 40), Path = Truncate(e.Path, 40) }).ToList();
            }
        }

        var finalSummary = new Summary(totalErrors, totalWarnings, curErrors.Count, curWarnings.Count, filesEvaluated);
        return JsonSerializer.Serialize(new BuildReport(status, exitCode, finalSummary, curErrors, curWarnings), JsonOptions);
    }

    private static string Truncate(string str, int maxLen) =>
        str.Length <= maxLen ? str : string.Concat(str.AsSpan(0, maxLen), "...");
}
