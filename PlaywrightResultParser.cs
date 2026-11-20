using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace InazumaPwsh;

public static class PlaywrightResultParser
{
    private static readonly Regex AnsiRegex = new(@"\x1B\[[0-?]*[ -/]*[@-~]", RegexOptions.Compiled);

    private record ErrorDetail([property: JsonPropertyName("message")] string Message, [property: JsonPropertyName("stack")] string Stack);
    private record Attachment([property: JsonPropertyName("name")] string Name, [property: JsonPropertyName("path")] string Path, [property: JsonPropertyName("contentType")] string ContentType);
    private record Failure([property: JsonPropertyName("title")] string Title, [property: JsonPropertyName("file")] string File, [property: JsonPropertyName("line")] int Line, [property: JsonPropertyName("column")] int Column, [property: JsonPropertyName("projectName")] string ProjectName, [property: JsonPropertyName("status")] string Status, [property: JsonPropertyName("expectedStatus")] string ExpectedStatus, [property: JsonPropertyName("retry")] int Retry, [property: JsonPropertyName("errors")] List<ErrorDetail> Errors, [property: JsonPropertyName("attachments")] List<Attachment> Attachments);
    private record Stats([property: JsonPropertyName("duration")] double Duration, [property: JsonPropertyName("passed")] int Passed, [property: JsonPropertyName("failed")] int Failed, [property: JsonPropertyName("flaky")] int Flaky, [property: JsonPropertyName("skipped")] int Skipped, [property: JsonPropertyName("total")] int Total);
    private record PlaywrightReport([property: JsonPropertyName("status")] string Status, [property: JsonPropertyName("exitCode")] int ExitCode, [property: JsonPropertyName("stats")] Stats Stats, [property: JsonPropertyName("globalErrors")] List<string> GlobalErrors, [property: JsonPropertyName("failures")] List<Failure> Failures, [property: JsonPropertyName(TestRegressionFilter.PassedTestsProperty)] List<string>? PassedTests);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static async Task<string> ParseAsync(string reportPath, string workingDirectory, int exitCode, bool includeWarnings = false, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(reportPath) || !File.Exists(reportPath))
        {
            var fallbackStatus = exitCode == 0 ? "report_unavailable" : "error";
            return JsonSerializer.Serialize(new PlaywrightReport(fallbackStatus, exitCode, new Stats(0, 0, 0, 0, 0, 0), [], [], null), JsonOptions);
        }

        try
        {
            if (new FileInfo(reportPath).Length > 50 * 1024 * 1024)
                return JsonSerializer.Serialize(new PlaywrightReport("report_unavailable", exitCode, new Stats(0, 0, 0, 0, 0, 0), ["Report file exceeded 50MB limit."], [], null), JsonOptions);

            await using var stream = new FileStream(reportPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                var fallbackStatus = exitCode == 0 ? "report_unavailable" : "error";
                return JsonSerializer.Serialize(new PlaywrightReport(fallbackStatus, exitCode, new Stats(0, 0, 0, 0, 0, 0), ["Root of Playwright report must be an object."], [], null), JsonOptions);
            }

            if (!doc.RootElement.TryGetProperty("suites", out var suites) || suites.ValueKind != JsonValueKind.Array)
            {
                var fallbackStatus = exitCode == 0 ? "report_unavailable" : "error";
                return JsonSerializer.Serialize(new PlaywrightReport(fallbackStatus, exitCode, new Stats(0, 0, 0, 0, 0, 0), ["Playwright report missing valid suites array schema."], [], null), JsonOptions);
            }

            var globalErrors = new List<string>();
            if (doc.RootElement.TryGetProperty("errors", out var rootErrs) && rootErrs.ValueKind == JsonValueKind.Array)
            {
                foreach (var re in rootErrs.EnumerateArray())
                {
                    var msg = ExtractMessage(re);
                    if (!string.IsNullOrWhiteSpace(msg)) globalErrors.Add(Truncate(StripAnsi(msg), 400));
                }
            }

            double duration = doc.RootElement.TryGetProperty("stats", out var statsElem) && statsElem.ValueKind == JsonValueKind.Object && statsElem.TryGetProperty("duration", out var dur) && dur.TryGetDouble(out var durVal) ? durVal : 0;
            int passed = 0, failed = 0, flaky = 0, skipped = 0;
            var failures = new List<Failure>();
            var passedTests = new List<string>();

            foreach (var suite in suites.EnumerateArray())
                TraverseSuite(suite, workingDirectory, failures, passedTests, ref passed, ref failed, ref flaky, ref skipped);

            int total = passed + failed + flaky + skipped;
            string status = total == 0
                ? (exitCode == 0 && globalErrors.Count == 0 ? "no_tests" : "error")
                : (exitCode != 0 || failed > 0 || globalErrors.Count > 0 ? "error" : "success");

            return SerializeWithLimit(status, exitCode, new Stats(duration, passed, failed, flaky, skipped, total), globalErrors, failures, passedTests);
        }
        catch (Exception ex) when (ex is JsonException or IOException or InvalidOperationException or FormatException or OverflowException)
        {
            var fallbackStatus = exitCode == 0 ? "report_unavailable" : "error";
            return JsonSerializer.Serialize(new PlaywrightReport(fallbackStatus, exitCode, new Stats(0, 0, 0, 0, 0, 0), [ex.Message], [], null), JsonOptions);
        }
    }

    private static void TraverseSuite(JsonElement suite, string workingDir, List<Failure> failures, List<string> passedTests, ref int passed, ref int failed, ref int flaky, ref int skipped)
    {
        if (suite.ValueKind != JsonValueKind.Object) return;
        var suiteFile = suite.TryGetProperty("file", out var sf) ? sf.GetString() ?? "" : "";
        if (suite.TryGetProperty("specs", out var specs) && specs.ValueKind == JsonValueKind.Array)
        {
            foreach (var spec in specs.EnumerateArray())
            {
                if (spec.ValueKind != JsonValueKind.Object) continue;
                var title = Truncate(spec.TryGetProperty("title", out var st) ? st.GetString() ?? "" : "", 300);
                var rawFile = spec.TryGetProperty("file", out var f) ? f.GetString() ?? suiteFile : suiteFile;
                var file = Truncate(ToRelativePath(rawFile, workingDir), 300);
                int line = spec.TryGetProperty("line", out var l) && l.TryGetInt32(out var lVal) ? lVal : 0;
                int col = spec.TryGetProperty("column", out var c) && c.TryGetInt32(out var cVal) ? cVal : 0;

                if (!spec.TryGetProperty("tests", out var tests) || tests.ValueKind != JsonValueKind.Array) continue;
                foreach (var test in tests.EnumerateArray())
                {
                    if (test.ValueKind != JsonValueKind.Object) continue;
                    var proj = Truncate(test.TryGetProperty("projectName", out var p) ? p.GetString() ?? "" : "", 100);
                    var status = test.TryGetProperty("status", out var s) ? s.GetString() ?? "" : "";
                    var expStatus = test.TryGetProperty("expectedStatus", out var es) ? es.GetString() ?? "passed" : "passed";

                    JsonElement lastResult = default;
                    var results = new List<JsonElement>();
                    if (test.TryGetProperty("results", out var res) && res.ValueKind == JsonValueKind.Array)
                        foreach (var r in res.EnumerateArray()) { if (r.ValueKind == JsonValueKind.Object) { results.Add(r); lastResult = r; } }

                    var lastStatus = lastResult.ValueKind != JsonValueKind.Undefined && lastResult.TryGetProperty("status", out var ls) ? ls.GetString() ?? "" : "";
                    bool isUnexp = status == "unexpected" || (string.IsNullOrEmpty(status) && lastStatus != expStatus && lastStatus != "skipped");
                    bool isFlaky = status == "flaky" || (string.IsNullOrEmpty(status) && results.Count > 1 && lastStatus == expStatus);
                    bool isSkip = status == "skipped" || lastStatus == "skipped";

                    if (isUnexp) failed++;
                    else if (isFlaky) flaky++;
                    else if (isSkip) skipped++;
                    else
                    {
                        passed++;
                        passedTests.Add(TestRegressionFilter.Identity(file, proj, title));
                    }

                    if (isUnexp && failures.Count < 10)
                    {
                        int retry = lastResult.ValueKind != JsonValueKind.Undefined && lastResult.TryGetProperty("retry", out var ret) && ret.TryGetInt32(out var retVal) ? retVal : 0;
                        failures.Add(new Failure(title, file, line, col, proj, string.IsNullOrEmpty(status) ? lastStatus : status, expStatus, retry, CollectErrors(results), CollectAttachments(results, workingDir)));
                    }
                }
            }
        }

        if (suite.TryGetProperty("suites", out var childSuites) && childSuites.ValueKind == JsonValueKind.Array)
            foreach (var child in childSuites.EnumerateArray()) TraverseSuite(child, workingDir, failures, passedTests, ref passed, ref failed, ref flaky, ref skipped);
    }

    private static List<ErrorDetail> CollectErrors(List<JsonElement> results)
    {
        var list = new List<ErrorDetail>();
        foreach (var res in results)
        {
            void AddErr(JsonElement el) {
                var m = Truncate(StripAnsi(ExtractMessage(el)), 500);
                var st = FilterStack(el.ValueKind == JsonValueKind.Object && el.TryGetProperty("stack", out var s) ? s.GetString() : null);
                if (m.Length > 0 || st.Length > 0) list.Add(new ErrorDetail(m, st));
            }
            if (res.TryGetProperty("errors", out var errs) && errs.ValueKind == JsonValueKind.Array)
                foreach (var err in errs.EnumerateArray()) AddErr(err);
            else if (res.TryGetProperty("error", out var singleErr)) AddErr(singleErr);
        }
        return list;
    }

    private static List<Attachment> CollectAttachments(List<JsonElement> results, string workingDir)
    {
        var list = new List<Attachment>();
        foreach (var res in results)
        {
            if (res.TryGetProperty("attachments", out var atts) && atts.ValueKind == JsonValueKind.Array)
                foreach (var att in atts.EnumerateArray())
                {
                    if (att.ValueKind != JsonValueKind.Object) continue;
                    var p = att.TryGetProperty("path", out var pathElem) ? pathElem.GetString() ?? "" : "";
                    if (!string.IsNullOrEmpty(p))
                    {
                        var name = Truncate(att.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "", 150);
                        var absolutePath = Path.GetFullPath(p, workingDir);
                        var ct = Truncate(att.TryGetProperty("contentType", out var c) ? c.GetString() ?? "" : "", 100);
                        list.Add(new Attachment(name, absolutePath, ct));
                    }
                }
        }
        return list;
    }

    private static string ExtractMessage(JsonElement el) => el.ValueKind == JsonValueKind.String ? el.GetString() ?? "" : (el.ValueKind == JsonValueKind.Object && el.TryGetProperty("message", out var m) ? m.GetString() ?? "" : "");

    internal static string FilterStack(string? stack)
    {
        if (string.IsNullOrWhiteSpace(stack)) return "";
        var lines = stack.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Select(l => Truncate(l.Trim(), 300)).Where(l => l.Length > 0);
        var user = lines.Where(l => !l.Contains("node_modules", StringComparison.OrdinalIgnoreCase) && !l.Contains("internal/", StringComparison.OrdinalIgnoreCase)).Take(4).ToList();
        return string.Join("\n", user.Count > 0 ? user : lines.Take(4));
    }

    internal static string StripAnsi(string? text) => string.IsNullOrEmpty(text) ? "" : AnsiRegex.Replace(text, "");

    internal static string ToRelativePath(string path, string workingDir)
    {
        if (string.IsNullOrWhiteSpace(path)) return "";
        try { return Path.IsPathRooted(path) && !string.IsNullOrWhiteSpace(workingDir) ? Path.GetRelativePath(workingDir, path).Replace('\\', '/') : path.Replace('\\', '/'); }
        catch { return path.Replace('\\', '/'); }
    }

    private static string SerializeWithLimit(string status, int exitCode, Stats stats, List<string> globalErrors, List<Failure> failures, List<string> passedTests)
    {
        var report = JsonSerializer.Serialize(new PlaywrightReport(status, exitCode, stats, globalErrors, failures, passedTests), JsonOptions);
        return DiagnosticBudget.Fit(report, 5500, "failures", "globalErrors");
    }

    internal static string ClipLines(string? str, int maxLines) => string.IsNullOrEmpty(str) ? "" : string.Join("\n", str.Split('\n').Take(maxLines));

    internal static string Truncate(string str, int maxLen) => str.Length <= maxLen ? str : string.Concat(str.AsSpan(0, maxLen), "...");
}
