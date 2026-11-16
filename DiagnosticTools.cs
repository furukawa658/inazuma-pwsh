using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Hosting;
using ModelContextProtocol.Server;

namespace InazumaPwsh;

[McpServerToolType]
public sealed class DiagnosticTools(IHostApplicationLifetime lifetime)
{
    [McpServerTool(Name = "run_dotnet_build")]
    [Description("Run dotnet build and return concise SARIF diagnostics and the exit code. The full log is saved to fullLogPath. Diagnostics for different projects are written to separate files.")]
    public Task<string> RunDotnetBuildAsync(
        [Description("Absolute path to the working directory.") ] string workingDirectory,
        [Description("Optional project or solution path, not a command string.") ] string? target = null,
        bool includeWarnings = false, int timeoutSeconds = 120, CancellationToken cancellationToken = default) =>
        RunAsync(workingDirectory, timeoutSeconds, async (directory, token) =>
        {
            var propsPath = Path.Combine(directory, "diagnostics.props");
            var diagnostics = System.Security.SecurityElement.Escape(directory.Replace('\\', '/'));
            var props = $"<Project><PropertyGroup><ErrorLog>{diagnostics}/$(MSBuildProjectName)-$(TargetFramework)-$(Configuration).sarif,version=2.1</ErrorLog></PropertyGroup></Project>";
            await File.WriteAllTextAsync(propsPath, props, token);
            var arguments = new List<string> { "build" };
            if (!string.IsNullOrEmpty(target))
            {
                if (target.StartsWith('-')) throw new ArgumentException("target must be a path, not an option.");
                arguments.Add(Path.GetFullPath(target, workingDirectory));
            }
            arguments.AddRange(["--nologo", "-v:q", $"-p:CustomAfterMicrosoftCommonTargets={propsPath}"]);
            var execution = await NativeProcessRunner.RunAsync("dotnet", arguments, workingDirectory, timeoutSeconds, null, token);
            var report = await DotnetBuildParser.ParseAsync(directory, workingDirectory, execution.ExitCode, includeWarnings, token);
            return await CombineAsync(report, execution);
        }, cancellationToken);

    [McpServerTool(Name = "run_playwright")]
    [Description("Run a locally installed Playwright and return failures, counts and paths to traces and other attachments from its JSON report. Dependencies are not installed automatically. Environment variables are set only in the child process.")]
    public Task<string> RunPlaywrightAsync(string workingDirectory,
        [Description("Arguments passed to Playwright test, with each argument as a separate array element.") ] string[]? testArgs = null,
        int timeoutSeconds = 180, CancellationToken cancellationToken = default) =>
        RunAsync(workingDirectory, timeoutSeconds, async (directory, token) =>
        {
            var cli = FindPlaywrightCli(workingDirectory);
            if (cli == null) throw new FileNotFoundException("Local Playwright CLI was not found. Install project dependencies first.");
            if (testArgs?.Any(argument => argument.StartsWith("--reporter", StringComparison.Ordinal)) == true)
                throw new ArgumentException("The JSON reporter is managed by this tool.");
            var reportPath = Path.Combine(directory, "playwright.json");
            var arguments = new List<string> { cli, "test" };
            arguments.AddRange(testArgs ?? []);
            arguments.Add("--reporter=json");
            var execution = await NativeProcessRunner.RunAsync("node", arguments, workingDirectory, timeoutSeconds,
                new Dictionary<string, string> { ["PLAYWRIGHT_JSON_OUTPUT_NAME"] = reportPath, ["FORCE_COLOR"] = "0", ["CI"] = "1" }, token);
            var report = await PlaywrightResultParser.ParseAsync(reportPath, workingDirectory, execution.ExitCode, cancellationToken: token);
            return await CombineAsync(report, execution);
        }, cancellationToken);

    [McpServerTool(Name = "run_bun_test")]
    [Description("Run bun test and return failures and counts from its JUnit XML report. The full log is saved to fullLogPath. Dependencies are not installed automatically.")]
    public Task<string> RunBunTestAsync(string workingDirectory,
        [Description("Arguments passed to bun test, with each argument as a separate array element.") ] string[]? testArgs = null,
        int timeoutSeconds = 180, CancellationToken cancellationToken = default) =>
        RunAsync(workingDirectory, timeoutSeconds, async (directory, token) =>
        {
            RejectManagedArguments(testArgs, "--reporter");
            var reportPath = Path.Combine(directory, "bun.xml");
            var arguments = new List<string> { "test" };
            arguments.AddRange(testArgs ?? []);
            arguments.AddRange(["--reporter=junit", $"--reporter-outfile={reportPath}"]);
            var execution = await NativeProcessRunner.RunAsync("bun", arguments, workingDirectory, timeoutSeconds,
                new Dictionary<string, string> { ["FORCE_COLOR"] = "0", ["NO_COLOR"] = "1" }, token);
            var report = await TestReportParser.ParseJUnitAsync(reportPath, workingDirectory, execution.ExitCode, cancellationToken: token);
            return await CombineAsync(report, execution);
        }, cancellationToken);

    [McpServerTool(Name = "run_pytest")]
    [Description("Run pytest and return failures and counts from its JUnit XML report. The interpreter is pythonPath, otherwise the nearest .venv found from workingDirectory upwards, otherwise python on PATH. Dependencies are not installed automatically.")]
    public Task<string> RunPytestAsync(string workingDirectory,
        [Description("Arguments passed to pytest, with each argument as a separate array element.") ] string[]? testArgs = null,
        [Description("Optional absolute path to the Python executable that has pytest installed.") ] string? pythonPath = null,
        int timeoutSeconds = 180, CancellationToken cancellationToken = default) =>
        RunAsync(workingDirectory, timeoutSeconds, async (directory, token) =>
        {
            RejectManagedArguments(testArgs, "--junitxml", "--junit-xml");
            var python = ResolvePython(workingDirectory, pythonPath);
            var reportPath = Path.Combine(directory, "pytest.xml");
            var arguments = new List<string> { "-m", "pytest" };
            arguments.AddRange(testArgs ?? []);
            arguments.Add($"--junitxml={reportPath}");
            var execution = await NativeProcessRunner.RunAsync(python, arguments, workingDirectory, timeoutSeconds,
                new Dictionary<string, string> { ["PYTHONUTF8"] = "1", ["PYTHONIOENCODING"] = "utf-8", ["NO_COLOR"] = "1" }, token);
            var report = await TestReportParser.ParseJUnitAsync(reportPath, workingDirectory, execution.ExitCode, noTestsExitCode: 5, cancellationToken: token);
            return await CombineAsync(report, execution);
        }, cancellationToken);

    [McpServerTool(Name = "run_vitest")]
    [Description("Run a locally installed Vitest once and return failures and counts from its JSON report. Dependencies are not installed automatically. Environment variables are set only in the child process.")]
    public Task<string> RunVitestAsync(string workingDirectory,
        [Description("Arguments passed to vitest run, with each argument as a separate array element.") ] string[]? testArgs = null,
        int timeoutSeconds = 180, CancellationToken cancellationToken = default) =>
        RunAsync(workingDirectory, timeoutSeconds, async (directory, token) =>
        {
            RejectManagedArguments(testArgs, "--reporter", "--outputFile", "--output-file");
            var cli = FindPackageFile(workingDirectory, ["vitest"], "vitest.mjs");
            if (cli == null) throw new FileNotFoundException("Local Vitest CLI was not found. Install project dependencies first.");
            var reportPath = Path.Combine(directory, "vitest.json");
            var arguments = new List<string> { cli, "run" };
            arguments.AddRange(testArgs ?? []);
            arguments.AddRange(["--reporter=json", $"--outputFile={reportPath}"]);
            var execution = await NativeProcessRunner.RunAsync("node", arguments, workingDirectory, timeoutSeconds,
                new Dictionary<string, string> { ["FORCE_COLOR"] = "0", ["CI"] = "1" }, token);
            var report = await TestReportParser.ParseVitestAsync(reportPath, workingDirectory, execution.ExitCode, token);
            return await CombineAsync(report, execution);
        }, cancellationToken);

    [McpServerTool(Name = "inspect_playwright_trace")]
    [Description("Inspect a trace ZIP without extracting it and return failed actions, HTTP and JavaScript errors, and a DOM excerpt before the failure. This does not replace visual verification of rendering or CSS.")]
    public Task<string> InspectPlaywrightTraceAsync(string tracePath, int maxOutputChars = 6000,
        CancellationToken cancellationToken = default) =>
        PlaywrightTraceParser.ParseAsync(tracePath, Math.Clamp(maxOutputChars, 1000, 50000), cancellationToken);

    private async Task<string> RunAsync(string workingDirectory, int timeoutSeconds,
        Func<string, CancellationToken, Task<string>> action, CancellationToken cancellationToken)
    {
        if (!Path.IsPathFullyQualified(workingDirectory) || !Directory.Exists(workingDirectory))
            return JsonDefaults.Serialize(new { status = "error", message = "workingDirectory must be an existing absolute directory." });
        var directory = Path.Combine(Path.GetTempPath(), $"inazuma-diagnostics-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.ApplicationStopping);
        try { return await action(directory, linked.Token); }
        catch (OperationCanceledException) { return JsonDefaults.Serialize(new { status = "cancelled" }); }
        catch (Exception exception) when (exception is IOException or ArgumentException or System.ComponentModel.Win32Exception)
        {
            return JsonDefaults.Serialize(new { status = "error", message = exception.Message });
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static string? FindPlaywrightCli(string workingDirectory) =>
        FindPackageFile(workingDirectory, ["@playwright/test", "playwright"], "cli.js");

    private static string? FindPackageFile(string workingDirectory, string[] packages, string fileName)
    {
        for (var directory = new DirectoryInfo(workingDirectory); directory != null; directory = directory.Parent)
            foreach (var package in packages)
            {
                var file = Path.Combine(directory.FullName, "node_modules", package, fileName);
                if (File.Exists(file)) return file;
            }
        return null;
    }

    private static void RejectManagedArguments(string[]? testArgs, params string[] prefixes)
    {
        if (testArgs?.Any(argument => prefixes.Any(prefix => argument.StartsWith(prefix, StringComparison.Ordinal))) == true)
            throw new ArgumentException($"{string.Join(", ", prefixes)} is managed by this tool.");
    }

    private static string ResolvePython(string workingDirectory, string? pythonPath)
    {
        if (!string.IsNullOrEmpty(pythonPath))
        {
            if (!Path.IsPathFullyQualified(pythonPath) || !File.Exists(pythonPath))
                throw new ArgumentException("pythonPath must be an existing absolute file path.");
            return pythonPath;
        }
        for (var directory = new DirectoryInfo(workingDirectory); directory != null; directory = directory.Parent)
        {
            var venvPython = Path.Combine(directory.FullName, ".venv", "Scripts", "python.exe");
            if (File.Exists(venvPython)) return venvPython;
        }
        return "python";
    }

    private static async Task<string> CombineAsync(string report, NativeProcessRunner.Result execution)
    {
        var result = JsonNode.Parse(report)!.AsObject();
        if (execution.Status != "completed") result["status"] = execution.Status;
        result["fullLogPath"] = execution.FullLogPath;
        result["durationMs"] = execution.DurationMs;
        if (result["status"]?.GetValue<string>() != "success")
            result["output"] = (await OutputCompressor.CompressAsync(execution.Tail, 1500)).Content;

        var json = result.ToJsonString(JsonDefaults.Options);
        if (json.Length <= 8000) return json;

        if (result.TryGetPropertyValue("output", out var outNode))
        {
            var outStr = outNode?.GetValue<string>() ?? "";
            if (outStr.Length > 300)
            {
                result["output"] = string.Concat(outStr.AsSpan(0, 300), "...");
                json = result.ToJsonString(JsonDefaults.Options);
                if (json.Length <= 8000) return json;
            }
        }

        string fullReportPath = Path.ChangeExtension(execution.FullLogPath, ".report.json");
        try
        {
            await File.WriteAllTextAsync(fullReportPath, report);
            result["fullReportPath"] = fullReportPath;
        }
        catch { }

        result.Remove("output");
        result.Remove("warnings");

        if (result.TryGetPropertyValue("failures", out var fNode) && fNode is JsonArray fArr && fArr.Count > 1)
        {
            var first = fArr[0]!.DeepClone();
            fArr.Clear();
            fArr.Add(first);
        }
        if (result.TryGetPropertyValue("errors", out var eNode) && eNode is JsonArray eArr && eArr.Count > 1)
        {
            var first = eArr[0]!.DeepClone();
            eArr.Clear();
            eArr.Add(first);
        }
        if (result.TryGetPropertyValue("globalErrors", out var gNode) && gNode is JsonArray gArr && gArr.Count > 1)
        {
            var first = gArr[0]!.DeepClone();
            gArr.Clear();
            gArr.Add(first);
        }

        json = result.ToJsonString(JsonDefaults.Options);
        if (json.Length <= 8000) return json;

        if (result.TryGetPropertyValue("failures", out var fNode2) && fNode2 is JsonArray fArr2 && fArr2.Count > 0 && fArr2[0] is JsonObject fObj)
        {
            fObj.Remove("attachments");
            if (fObj.TryGetPropertyValue("errors", out var errNode) && errNode is JsonArray errArr && errArr.Count > 0 && errArr[0] is JsonObject errObj)
            {
                errObj.Remove("stack");
                if (errObj.TryGetPropertyValue("message", out var mNode))
                {
                    var m = mNode?.GetValue<string>() ?? "";
                    if (m.Length > 80) errObj["message"] = string.Concat(m.AsSpan(0, 80), "...");
                }
            }
        }
        if (result.TryGetPropertyValue("errors", out var eNode2) && eNode2 is JsonArray eArr2 && eArr2.Count > 0 && eArr2[0] is JsonObject eObj)
        {
            if (eObj.TryGetPropertyValue("message", out var emNode))
            {
                var em = emNode?.GetValue<string>() ?? "";
                if (em.Length > 80) eObj["message"] = string.Concat(em.AsSpan(0, 80), "...");
            }
        }

        json = result.ToJsonString(JsonDefaults.Options);
        if (json.Length <= 8000) return json;
        return JsonDefaults.Serialize(new
        {
            status = result["status"]?.GetValue<string>() is { } status ? status[..Math.Min(status.Length, 50)] : "error",
            exitCode = execution.ExitCode,
            durationMs = execution.DurationMs,
            fullLogPath = execution.FullLogPath[..Math.Min(execution.FullLogPath.Length, 256)],
            fullReportPath = File.Exists(fullReportPath) ? fullReportPath[..Math.Min(fullReportPath.Length, 256)] : null,
            diagnosticPreview = report[..Math.Min(report.Length, 300)],
            isTruncated = true
        });
    }
}