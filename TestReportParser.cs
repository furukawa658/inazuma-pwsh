using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;

namespace InazumaPwsh;

public static class TestReportParser
{
    private const long MaxReportBytes = 50L * 1024 * 1024;
    private const int MaxFailures = 10;
    private static readonly Regex LocationRegex = new(@"(?<file>(?:[A-Za-z]:[\\/])?[^\s:""'()<>]+\.[A-Za-z]{1,5}):(?<line>\d+)", RegexOptions.Compiled);
    private static readonly Regex PythonLocationRegex = new(@"file (?<file>[^\r\n]+?), line (?<line>\d+)", RegexOptions.Compiled);
    private static readonly Regex StackLineRegex = new(@"^(at\s|[^\s:]+\.[A-Za-z]{1,5}:\d+:)", RegexOptions.Compiled);
    // Matches "in C:\src\T.cs:line 12" without depending on the localized words around the path.
    private static readonly Regex DotnetLocationRegex = new(@"\s(?<file>(?:[A-Za-z]:[\\/]|/)[^:\r\n]*?\.[A-Za-z]{1,5}):\S+ (?<line>\d+)\s*$", RegexOptions.Compiled | RegexOptions.Multiline);

    private record ErrorDetail([property: JsonPropertyName("message")] string Message, [property: JsonPropertyName("stack")] string Stack);
    private record Failure([property: JsonPropertyName("title")] string Title, [property: JsonPropertyName("file")] string File, [property: JsonPropertyName("line")] int Line, [property: JsonPropertyName("status")] string Status, [property: JsonPropertyName("errors")] List<ErrorDetail> Errors, [property: JsonPropertyName("count")] int? Count = null);
    private record Stats([property: JsonPropertyName("duration")] double Duration, [property: JsonPropertyName("passed")] int Passed, [property: JsonPropertyName("failed")] int Failed, [property: JsonPropertyName("skipped")] int Skipped, [property: JsonPropertyName("total")] int Total, [property: JsonPropertyName("errors")] int Errors = 0);
    private record TestReport([property: JsonPropertyName("status")] string Status, [property: JsonPropertyName("exitCode")] int ExitCode, [property: JsonPropertyName("stats")] Stats Stats, [property: JsonPropertyName("globalErrors")] List<string> GlobalErrors, [property: JsonPropertyName("failures")] List<Failure> Failures, [property: JsonPropertyName(TestRegressionFilter.PassedTestsProperty)] List<string>? PassedTests = null);

    private sealed class Accumulator
    {
        public int Passed, Failed, Skipped, Errors;
        public double Duration;
        public List<string> GlobalErrors { get; } = [];
        public List<Failure> Failures { get; } = [];
        public List<string> PassedTests { get; } = [];

        /// <summary>Failures at the same location with the same message are kept once with a count.</summary>
        public void AddFailure(Failure failure)
        {
            var message = failure.Errors.FirstOrDefault()?.Message;
            var index = failure.Line > 0
                ? Failures.FindIndex(f => f.Line == failure.Line && f.File == failure.File && f.Errors.FirstOrDefault()?.Message == message)
                : -1;
            if (index >= 0) Failures[index] = Failures[index] with { Count = (Failures[index].Count ?? 1) + 1 };
            else if (Failures.Count < MaxFailures) Failures.Add(failure);
        }
    }

    private static XmlReaderSettings XmlSettings => new() { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, Async = true, IgnoreComments = true };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// <summary>Parses a JUnit XML report such as those written by bun test and pytest.</summary>
    public static Task<string> ParseJUnitAsync(string reportPath, string workingDirectory, int exitCode, int? noTestsExitCode = null, CancellationToken cancellationToken = default) =>
        ParseAsync(reportPath, exitCode, noTestsExitCode, cancellationToken, async (stream, token) =>
        {
            using var reader = XmlReader.Create(stream, XmlSettings);
            var document = await XDocument.LoadAsync(reader, LoadOptions.None, token);
            var root = document.Root ?? throw new FormatException("JUnit report has no root element.");
            if (root.Name.LocalName is not ("testsuites" or "testsuite"))
                throw new FormatException("JUnit report root must be testsuites or testsuite.");

            var result = new Accumulator { Duration = ParseSeconds(root.Attribute("time")?.Value) * 1000 };
            if (result.Duration == 0 && root.Name.LocalName == "testsuites")
                result.Duration = root.Elements().Where(e => e.Name.LocalName == "testsuite").Sum(e => ParseSeconds(e.Attribute("time")?.Value)) * 1000;

            foreach (var testCase in root.DescendantsAndSelf().Where(e => e.Name.LocalName == "testcase"))
            {
                token.ThrowIfCancellationRequested();
                var problem = testCase.Elements().FirstOrDefault(e => e.Name.LocalName is "failure" or "error");
                if (problem != null)
                {
                    var message = problem.Attribute("message")?.Value ?? "";
                    var isError = problem.Name.LocalName == "error" || (noTestsExitCode == 5
                        && Regex.IsMatch(message, @"^(?!AssertionError\b)(?:[\w.]*\.)?[A-Za-z_]\w*(?:Error|Exception)(?::|$)"));
                    if (isError) result.Errors++;
                    else result.Failed++;
                    result.AddFailure(ToFailure(testCase, problem, workingDirectory) with { Status = isError ? "error" : "failed" });
                }
                else if (testCase.Elements().Any(e => e.Name.LocalName == "skipped")) result.Skipped++;
                else
                {
                    result.Passed++;
                    var name = testCase.Attribute("name")?.Value ?? "";
                    var className = testCase.Attribute("classname")?.Value ?? "";
                    var title = className.Length > 0 && className != name ? $"{className} > {name}" : name;
                    var file = testCase.Attribute("file")?.Value ?? "";
                    result.PassedTests.Add(TestRegressionFilter.Identity(PlaywrightResultParser.Truncate(PlaywrightResultParser.ToRelativePath(file, workingDirectory), 300), null, title));
                }
            }
            return result;
        });

    /// <summary>Parses the JSON report written by the Vitest json reporter.</summary>
    public static Task<string> ParseVitestAsync(string reportPath, string workingDirectory, int exitCode, CancellationToken cancellationToken = default) =>
        ParseAsync(reportPath, exitCode, null, cancellationToken, async (stream, token) =>
        {
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: token);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("testResults", out var files) || files.ValueKind != JsonValueKind.Array)
                throw new FormatException("Vitest report missing valid testResults array schema.");

            var result = new Accumulator();
            double start = root.TryGetProperty("startTime", out var st) && st.TryGetDouble(out var stVal) ? stVal : 0, end = 0;
            foreach (var file in files.EnumerateArray())
            {
                token.ThrowIfCancellationRequested();
                if (file.ValueKind != JsonValueKind.Object) continue;
                var rawFile = GetString(file, "name");
                var relativeFile = PlaywrightResultParser.Truncate(PlaywrightResultParser.ToRelativePath(rawFile, workingDirectory), 300);
                if (file.TryGetProperty("endTime", out var et) && et.TryGetDouble(out var etVal)) end = Math.Max(end, etVal);

                var assertions = file.TryGetProperty("assertionResults", out var a) && a.ValueKind == JsonValueKind.Array ? a.EnumerateArray().ToList() : [];
                var fileMessage = PlaywrightResultParser.StripAnsi(GetString(file, "message"));
                if (GetString(file, "status") == "failed" && fileMessage.Length > 0 && !assertions.Any(x => GetString(x, "status") == "failed"))
                    result.GlobalErrors.Add(PlaywrightResultParser.Truncate($"{relativeFile}: {fileMessage}", 400));

                foreach (var assertion in assertions)
                {
                    if (assertion.ValueKind != JsonValueKind.Object) continue;
                    var status = GetString(assertion, "status");
                    var title = GetString(assertion, "fullName");
                    if (title.Length == 0) title = GetString(assertion, "title");
                    if (status == "passed")
                    {
                        result.Passed++;
                        result.PassedTests.Add(TestRegressionFilter.Identity(relativeFile, null, PlaywrightResultParser.Truncate(title, 300)));
                        continue;
                    }
                    if (status != "failed") { result.Skipped++; continue; }
                    result.Failed++;
                    var messages = assertion.TryGetProperty("failureMessages", out var fm) && fm.ValueKind == JsonValueKind.Array
                        ? fm.EnumerateArray().Where(m => m.ValueKind == JsonValueKind.String).Select(m => PlaywrightResultParser.StripAnsi(m.GetString())).ToList() : [];
                    int line = assertion.TryGetProperty("location", out var loc) && loc.ValueKind == JsonValueKind.Object && loc.TryGetProperty("line", out var l) && l.TryGetInt32(out var lVal) ? lVal : 0;
                    if (line == 0)
                        foreach (var message in messages)
                            if (FindLocation(message, Path.GetFileName(rawFile)) is { } found) { line = found.Line; break; }
                    result.AddFailure(new Failure(PlaywrightResultParser.Truncate(title, 300), relativeFile, line, status, messages.Select(SplitError).Take(3).ToList()));
                }
            }
            if (start > 0 && end >= start) result.Duration = end - start;
            return result;
        });

    /// <summary>Parses every TRX report that dotnet test wrote under a results directory.</summary>
    public static async Task<string> ParseTrxAsync(string resultsDirectory, string workingDirectory, int exitCode, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var fallbackStatus = exitCode == 0 ? "report_unavailable" : "error";
        var files = Directory.Exists(resultsDirectory)
            ? Directory.EnumerateFiles(resultsDirectory, "*.trx", SearchOption.AllDirectories).Take(100).ToList() : [];
        if (files.Count == 0) return Serialize(new TestReport(fallbackStatus, exitCode, new Stats(0, 0, 0, 0, 0), [], []));
        try
        {
            var result = new Accumulator();
            var rerunNames = new HashSet<string>(StringComparer.Ordinal);
            foreach (var file in files)
            {
                if (new FileInfo(file).Length > MaxReportBytes) throw new FormatException("Report file exceeded 50MB limit.");
                await using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = XmlReader.Create(stream, XmlSettings);
                var root = (await XDocument.LoadAsync(reader, LoadOptions.None, cancellationToken)).Root;
                if (root?.Name.LocalName != "TestRun") throw new FormatException("TRX report root must be TestRun.");

                var testNames = root.Descendants().Where(e => e.Name.LocalName == "UnitTest")
                    .Where(e => e.Attribute("id") != null)
                    .GroupBy(e => e.Attribute("id")!.Value).ToDictionary(group => group.Key, group =>
                    {
                        var method = group.First().Elements().FirstOrDefault(e => e.Name.LocalName == "TestMethod");
                        var className = (method?.Attribute("className")?.Value ?? "").Split(',')[0].Trim();
                        var name = method?.Attribute("name")?.Value ?? "";
                        return className.Length > 0 && name.Length > 0 ? $"{className}.{name}" : "";
                    });
                foreach (var info in root.Descendants().Where(e => e.Name.LocalName == "RunInfo" && e.Attribute("outcome")?.Value == "Error"))
                    if (!info.Value.Contains("[FAIL]", StringComparison.OrdinalIgnoreCase))
                        result.GlobalErrors.Add(PlaywrightResultParser.Truncate(info.Value.Trim(), 400));
                foreach (var item in root.Descendants().Where(e => e.Name.LocalName == "UnitTestResult" && !e.Ancestors().Any(a => a.Name.LocalName == "UnitTestResult")))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (TimeSpan.TryParse(item.Attribute("duration")?.Value, CultureInfo.InvariantCulture, out var duration)) result.Duration += duration.TotalMilliseconds;
                    var outcome = item.Attribute("outcome")?.Value ?? "";
                    if (outcome == "Passed")
                    {
                        result.Passed++;
                        result.PassedTests.Add(TestRegressionFilter.Identity("", null, item.Attribute("testName")?.Value ?? ""));
                        continue;
                    }
                    if (outcome is not ("Failed" or "Error" or "Timeout" or "Aborted")) { result.Skipped++; continue; }
                    result.Failed++;

                    var testId = item.Attribute("testId")?.Value ?? "";
                    var testName = testNames.GetValueOrDefault(testId);
                    if (string.IsNullOrEmpty(testName)) testName = (item.Attribute("testName")?.Value ?? "").Split('(')[0].Trim();
                    if (!string.IsNullOrEmpty(testName)) rerunNames.Add(testName);

                    var error = item.Descendants().FirstOrDefault(e => e.Name.LocalName == "ErrorInfo");
                    var message = (error?.Elements().FirstOrDefault(e => e.Name.LocalName == "Message")?.Value.Trim() ?? "").Replace("\r\n", "\n").Replace('\r', '\n');
                    var stack = error?.Elements().FirstOrDefault(e => e.Name.LocalName == "StackTrace")?.Value ?? "";
                    var location = DotnetLocationRegex.Match(stack);
                    var stackLines = string.Join("\n", stack.Split(['\r', '\n']).Select(l => l.Trim()).Where(l => l.Length > 0
                        && !l.StartsWith("--- End of stack trace", StringComparison.OrdinalIgnoreCase)
                        && !Regex.IsMatch(l, @"^at\s+(?:System\.Reflection\.|System\.RuntimeMethodHandle\.|Xunit\.|Xunit\b|xunit\.)", RegexOptions.IgnoreCase)));
                    result.AddFailure(new Failure(
                        PlaywrightResultParser.Truncate(item.Attribute("testName")?.Value ?? "", 300),
                        location.Success ? PlaywrightResultParser.Truncate(PlaywrightResultParser.ToRelativePath(location.Groups["file"].Value, workingDirectory), 300) : "",
                        location.Success && int.TryParse(location.Groups["line"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var line) ? line : 0,
                        "failed", [new ErrorDetail(PlaywrightResultParser.Truncate(message, 500), PlaywrightResultParser.Truncate(stackLines, 2000))]));
                }
            }
            var report = JsonNode.Parse(Finish(result, exitCode, null))!.AsObject();
            if (result.Failed > 0 && rerunNames.Count > 0 && result.GlobalErrors.Count == 0)
            {
                var filter = string.Join("|", rerunNames.Select(name => "FullyQualifiedName~" + Regex.Replace(name, @"[\\&|=!~]", @"\$0")));
                report["rerunArgs"] = new JsonArray("--filter", filter);
            }
            return report.ToJsonString(JsonDefaults.Options);
        }
        catch (Exception ex) when (ex is XmlException or IOException or InvalidOperationException or FormatException or OverflowException)
        {
            return Serialize(new TestReport(fallbackStatus, exitCode, new Stats(0, 0, 0, 0, 0), [PlaywrightResultParser.Truncate(ex.Message, 400)], []));
        }
    }

    private static string Finish(Accumulator result, int exitCode, int? noTestsExitCode)
    {
        int total = result.Passed + result.Failed + result.Errors + result.Skipped;
        bool noTests = exitCode == 0 || exitCode == noTestsExitCode;
        string status = total == 0
            ? (noTests && result.GlobalErrors.Count == 0 ? "no_tests" : "error")
            : (exitCode != 0 || result.Failed > 0 || result.Errors > 0 || result.GlobalErrors.Count > 0 ? "error" : "success");
        return SerializeWithLimit(status, exitCode, new Stats(Math.Round(result.Duration, 1), result.Passed, result.Failed, result.Skipped, total, result.Errors), result.GlobalErrors, result.Failures, result.PassedTests);
    }

    private static async Task<string> ParseAsync(string reportPath, int exitCode, int? noTestsExitCode, CancellationToken cancellationToken,
        Func<Stream, CancellationToken, Task<Accumulator>> parse)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var fallbackStatus = exitCode == 0 ? "report_unavailable" : "error";
        if (string.IsNullOrWhiteSpace(reportPath) || !File.Exists(reportPath))
            return Serialize(new TestReport(fallbackStatus, exitCode, new Stats(0, 0, 0, 0, 0), [], []));
        if (new FileInfo(reportPath).Length > MaxReportBytes)
            return Serialize(new TestReport("report_unavailable", exitCode, new Stats(0, 0, 0, 0, 0), ["Report file exceeded 50MB limit."], []));
        try
        {
            Accumulator result;
            await using (var stream = new FileStream(reportPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                result = await parse(stream, cancellationToken);
            return Finish(result, exitCode, noTestsExitCode);
        }
        catch (Exception ex) when (ex is JsonException or XmlException or IOException or InvalidOperationException or FormatException or OverflowException)
        {
            return Serialize(new TestReport(fallbackStatus, exitCode, new Stats(0, 0, 0, 0, 0), [PlaywrightResultParser.Truncate(ex.Message, 400)], []));
        }
    }

    private static Failure ToFailure(XElement testCase, XElement problem, string workingDirectory)
    {
        var name = testCase.Attribute("name")?.Value ?? "";
        var className = testCase.Attribute("classname")?.Value ?? "";
        var title = className.Length > 0 && className != name ? $"{className} > {name}" : name;
        var text = PlaywrightResultParser.StripAnsi(problem.Value);
        var message = PlaywrightResultParser.StripAnsi(problem.Attribute("message")?.Value);
        var split = SplitError(text);
        if (message.Length == 0) message = split.Message;

        var file = testCase.Attribute("file")?.Value ?? "";
        int line = int.TryParse(testCase.Attribute("line")?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0;
        if (file.Length == 0 || line == 0)
        {
            var found = FindLocation(text, file.Length > 0 ? Path.GetFileName(file) : null) ?? FindLocation(message, null);
            if (found is { } location)
            {
                if (file.Length == 0) file = location.File;
                if (line == 0) line = location.Line;
            }
        }
        return new Failure(PlaywrightResultParser.Truncate(title, 300),
            PlaywrightResultParser.Truncate(PlaywrightResultParser.ToRelativePath(file, workingDirectory), 300), line, problem.Name.LocalName == "error" ? "error" : "failed",
            [new ErrorDetail(PlaywrightResultParser.Truncate(message.Trim(), 500), split.Stack)]);
    }

    private static ErrorDetail SplitError(string text)
    {
        var lines = text.Split(['\r', '\n']);
        var stackStart = Array.FindIndex(lines, l => StackLineRegex.IsMatch(l.Trim()));
        var message = string.Join("\n", (stackStart < 0 ? lines : lines.Take(stackStart)).Where(l => l.Trim().Length > 0));
        var stack = stackStart < 0 ? "" : PlaywrightResultParser.FilterStack(string.Join("\n", lines.Skip(stackStart)
            .Where(l => StackLineRegex.IsMatch(l.Trim()) && !l.Contains("<anonymous>", StringComparison.Ordinal) && !l.Contains("node:internal", StringComparison.Ordinal))));
        return new ErrorDetail(PlaywrightResultParser.Truncate(message, 500), stack);
    }

    private static (string File, int Line)? FindLocation(string text, string? preferredFileName)
    {
        if (string.IsNullOrEmpty(text)) return null;
        (string File, int Line)? fallback = null;
        foreach (Match match in LocationRegex.Matches(text).Concat(PythonLocationRegex.Matches(text)))
        {
            var file = match.Groups["file"].Value;
            if (file.Contains("node_modules", StringComparison.OrdinalIgnoreCase) || file.Contains("site-packages", StringComparison.OrdinalIgnoreCase)) continue;
            if (!int.TryParse(match.Groups["line"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var line)) continue;
            if (preferredFileName == null || Path.GetFileName(file).Equals(preferredFileName, StringComparison.OrdinalIgnoreCase)) return (file, line);
            fallback ??= (file, line);
        }
        return preferredFileName == null ? fallback : null;
    }

    private static double ParseSeconds(string? value) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) && seconds >= 0 ? seconds : 0;

    private static string GetString(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";

    private static string Serialize(TestReport report) => JsonSerializer.Serialize(report, JsonOptions);

    private static string SerializeWithLimit(string status, int exitCode, Stats stats, List<string> globalErrors, List<Failure> failures, List<string>? passedTests = null)
    {
        var report = Serialize(new TestReport(status, exitCode, stats, globalErrors, failures, passedTests));
        return DiagnosticBudget.Fit(report, 5500, "failures", "globalErrors");
    }
}
