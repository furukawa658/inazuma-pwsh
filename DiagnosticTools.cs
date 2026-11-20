using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Hosting;
using ModelContextProtocol.Server;

namespace InazumaPwsh;

[McpServerToolType]
public sealed class DiagnosticTools(IHostApplicationLifetime lifetime)
{
    private const string ContextLinesText = "Number of source lines (0 to 10) shown before and after each failure location as codeContext. 0 omits it. A line already shown for an earlier item of the same file is not repeated.";
    private const string CompactResponseText = "When true (default), omit output lines already represented in structured fields. Set false to retain raw output.";
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, HashSet<string>> TscBaselines = new();
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, HashSet<string>> DotnetBuildBaselines = new();
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, HashSet<string>> EslintBaselines = new();
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, HashSet<string>> CargoCheckBaselines = new();

    [McpServerTool(Name = "run_dotnet_build")]
    [Description("Run dotnet build and return concise SARIF diagnostics and the exit code. The full log is saved to fullLogPath. Diagnostics for different projects are written to separate files.")]
    public Task<string> RunDotnetBuildAsync(
        [Description("Absolute path to the working directory.") ] string workingDirectory,
        [Description("Optional project or solution path, not a command string.") ] string? target = null,
        bool includeWarnings = false,
        [Description("When true, list only diagnostics absent from the previous run with the same workingDirectory, target, and warning setting in this server process, and add newCount and fixedCount.")] bool onlyNew = false,
        int timeoutSeconds = 120, [Description(ContextLinesText)] int contextLines = 0, [Description(CompactResponseText)] bool compactResponse = true, CancellationToken cancellationToken = default) =>
        RunAsync("run_dotnet_build", workingDirectory, timeoutSeconds, async (directory, token) =>
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
            var baselineKey = workingDirectory + "\n" + (target ?? "") + "\n" + includeWarnings;
            DotnetBuildBaselines.TryGetValue(baselineKey, out var previous);
            var parsed = await DotnetBuildParser.ParseAsync(directory, workingDirectory, execution.ExitCode, includeWarnings, token, execution.FullLogPath, onlyNew ? previous : null);
            if (execution.Status == "completed" && parsed.Baseline != null) UpdateBaseline(DotnetBuildBaselines, baselineKey, parsed.Baseline);
            return await CombineAsync("run_dotnet_build", parsed.Report, execution, workingDirectory, contextLines, compactResponse: compactResponse);
        }, cancellationToken);

    [McpServerTool(Name = "run_dotnet_test")]
    [Description("Run dotnet test and return failures and counts from its TRX reports. Uses VSTest options, or Microsoft.Testing.Platform options when global.json selects that runner (the test project then needs the TRX report extension). Dependencies are not installed automatically.")]
    public Task<string> RunDotnetTestAsync(string workingDirectory,
        [Description("Arguments passed to dotnet test, with each argument as a separate array element, for example a project path or --filter.") ] string[]? testArgs = null,
        int timeoutSeconds = 180, [Description(ContextLinesText)] int contextLines = 0, [Description(CompactResponseText)] bool compactResponse = true, CancellationToken cancellationToken = default) =>
        RunAsync("run_dotnet_test", workingDirectory, timeoutSeconds, async (directory, token) =>
        {
            RejectManagedArguments(testArgs, "--logger", "-l", "--results-directory", "--report-trx");
            var testingPlatform = UsesTestingPlatform(workingDirectory);
            var arguments = new List<string> { "test" };
            arguments.AddRange(testingPlatform ? ["--report-trx"] : ["--nologo", "--logger", "trx"]);
            arguments.AddRange(["--results-directory", directory]);
            arguments.AddRange(testArgs ?? []);
            var execution = await NativeProcessRunner.RunAsync("dotnet", arguments, workingDirectory, timeoutSeconds,
                new Dictionary<string, string> { ["DOTNET_CLI_UI_LANGUAGE"] = "en", ["DOTNET_NOLOGO"] = "1" }, token);
            var report = await TestReportParser.ParseTrxAsync(directory, workingDirectory, execution.ExitCode, token);
            if (testingPlatform) { var parsed = JsonNode.Parse(report)!.AsObject(); parsed.Remove("rerunArgs"); report = parsed.ToJsonString(JsonDefaults.Options); }
            return await CombineAsync("run_dotnet_test", report, execution, workingDirectory, contextLines, testingPlatform ? null : DotnetFilter, compactResponse: compactResponse);
        }, cancellationToken);

    [McpServerTool(Name = "run_playwright")]
    [Description("Run a locally installed Playwright and return failures, counts and paths to traces and other attachments from its JSON report. Dependencies are not installed automatically. Environment variables are set only in the child process.")]
    public Task<string> RunPlaywrightAsync(string workingDirectory,
        [Description("Arguments passed to Playwright test, with each argument as a separate array element.") ] string[]? testArgs = null,
        int timeoutSeconds = 180, [Description(ContextLinesText)] int contextLines = 0, [Description(CompactResponseText)] bool compactResponse = true, CancellationToken cancellationToken = default) =>
        RunAsync("run_playwright", workingDirectory, timeoutSeconds, async (directory, token) =>
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
            return await CombineAsync("run_playwright", report, execution, workingDirectory, contextLines, result => HasFailures(result) ? ["--last-failed"] : null, compactResponse: compactResponse);
        }, cancellationToken);

    [McpServerTool(Name = "run_bun_test")]
    [Description("Run bun test and return failures and counts from its JUnit XML report. The full log is saved to fullLogPath. Dependencies are not installed automatically.")]
    public Task<string> RunBunTestAsync(string workingDirectory,
        [Description("Arguments passed to bun test, with each argument as a separate array element.") ] string[]? testArgs = null,
        int timeoutSeconds = 180, [Description(ContextLinesText)] int contextLines = 0, [Description(CompactResponseText)] bool compactResponse = true, CancellationToken cancellationToken = default) =>
        RunAsync("run_bun_test", workingDirectory, timeoutSeconds, async (directory, token) =>
        {
            RejectManagedArguments(testArgs, "--reporter");
            var reportPath = Path.Combine(directory, "bun.xml");
            var arguments = new List<string> { "test" };
            arguments.AddRange(testArgs ?? []);
            arguments.AddRange(["--reporter=junit", $"--reporter-outfile={reportPath}"]);
            var execution = await NativeProcessRunner.RunAsync("bun", arguments, workingDirectory, timeoutSeconds,
                new Dictionary<string, string> { ["FORCE_COLOR"] = "0", ["NO_COLOR"] = "1" }, token);
            var report = await TestReportParser.ParseJUnitAsync(reportPath, workingDirectory, execution.ExitCode, cancellationToken: token);
            if (execution.ExitCode != 0)
            {
                var parsed = JsonNode.Parse(report)!.AsObject();
                if (parsed["globalErrors"] is JsonArray { Count: 0 } globals)
                {
                    var noTests = (parsed["stats"]?["total"]?.GetValue<int>() ?? 0) == 0;
                    foreach (var line in execution.Tail.Split('\n').Select(line => PlaywrightResultParser.StripAnsi(line).Trim())
                        .Where(line => line.Contains("error", StringComparison.OrdinalIgnoreCase) && (noTests || line.Contains("matched 0 tests", StringComparison.OrdinalIgnoreCase))).Take(5)) globals.Add(line);
                    if (globals.Count > 0) { parsed["status"] = "error"; report = parsed.ToJsonString(JsonDefaults.Options); }
                }
            }
            return await CombineAsync("run_bun_test", report, execution, workingDirectory, contextLines, FailedFiles, compactResponse: compactResponse);
        }, cancellationToken);

    [McpServerTool(Name = "run_pytest")]
    [Description("Run pytest and return failures and counts from its JUnit XML report. The interpreter is pythonPath, otherwise the nearest .venv found from workingDirectory upwards, otherwise python on PATH. Dependencies are not installed automatically.")]
    public Task<string> RunPytestAsync(string workingDirectory,
        [Description("Arguments passed to pytest, with each argument as a separate array element.") ] string[]? testArgs = null,
        [Description("Optional absolute path to the Python executable that has pytest installed.") ] string? pythonPath = null,
        int timeoutSeconds = 180, [Description(ContextLinesText)] int contextLines = 0, [Description(CompactResponseText)] bool compactResponse = true, CancellationToken cancellationToken = default) =>
        RunAsync("run_pytest", workingDirectory, timeoutSeconds, async (directory, token) =>
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
            return await CombineAsync("run_pytest", report, execution, workingDirectory, contextLines, result => HasFailures(result) ? ["--lf"] : null, compactResponse: compactResponse);
        }, cancellationToken);

    [McpServerTool(Name = "run_vitest")]
    [Description("Run a locally installed Vitest once and return failures and counts from its JSON report. Dependencies are not installed automatically. Environment variables are set only in the child process.")]
    public Task<string> RunVitestAsync(string workingDirectory,
        [Description("Arguments passed to vitest run, with each argument as a separate array element.") ] string[]? testArgs = null,
        int timeoutSeconds = 180, [Description(ContextLinesText)] int contextLines = 0, [Description(CompactResponseText)] bool compactResponse = true, CancellationToken cancellationToken = default) =>
        RunAsync("run_vitest", workingDirectory, timeoutSeconds, async (directory, token) =>
        {
            RejectManagedArguments(testArgs, "--reporter", "--outputFile", "--output-file");
            var cli = FindPackageFile(workingDirectory, ["vitest"], "vitest.mjs");
            if (cli == null) throw new FileNotFoundException("Local Vitest CLI was not found. Install project dependencies first.");
            var reportPath = Path.Combine(directory, "vitest.json");
            var arguments = new List<string> { cli, "run" };
            arguments.AddRange(testArgs ?? []);
            arguments.AddRange(["--reporter=default", "--reporter=json", $"--outputFile.json={reportPath}"]);
            var execution = await NativeProcessRunner.RunAsync("node", arguments, workingDirectory, timeoutSeconds,
                new Dictionary<string, string> { ["FORCE_COLOR"] = "0", ["CI"] = "1" }, token);
            var report = await TestReportParser.ParseVitestAsync(reportPath, workingDirectory, execution.ExitCode, token);
            return await CombineAsync("run_vitest", report, execution, workingDirectory, contextLines, FailedFiles, compactResponse: compactResponse, includeSuccessOutput: true);
        }, cancellationToken);

    [McpServerTool(Name = "run_tsc")]
    [Description("Run a locally installed TypeScript compiler with --noEmit and return error counts, error-code counts and the first 15 diagnostics. output leaves out the compiler lines that diagnostics already lists in full. Passing --build or -b in tscArgs builds project references and omits --noEmit. tscPath overrides the compiler found in node_modules/typescript. Dependencies are not installed automatically.")]
    public Task<string> RunTscAsync(string workingDirectory,
        [Description("Arguments passed to tsc, with each argument as a separate array element, for example -p tsconfig.app.json.") ] string[]? tscArgs = null,
        [Description("Optional absolute path to tsc.js.") ] string? tscPath = null,
        int timeoutSeconds = 180, [Description(ContextLinesText)] int contextLines = 0,
        [Description("When true, list only diagnostics absent from the previous run with the same workingDirectory and tscArgs in this server process, and add newCount and fixedCount.")] bool onlyNew = false,
        [Description(CompactResponseText)] bool compactResponse = true,
        CancellationToken cancellationToken = default) =>
        RunAsync("run_tsc", workingDirectory, timeoutSeconds, async (directory, token) =>
        {
            RejectManagedArguments(tscArgs, "--pretty", "--noEmit", "--watch", "-w", "--preserveWatchOutput", "--init");
            var build = tscArgs?.Any(argument => argument is "--build" or "-b") == true;
            string? cli = tscPath;
            if (!string.IsNullOrEmpty(cli))
            {
                if (!Path.IsPathFullyQualified(cli) || !File.Exists(cli))
                    throw new ArgumentException("tscPath must be an existing absolute file path.");
            }
            else cli = FindPackageFile(workingDirectory, ["typescript"], Path.Combine("lib", "tsc.js"));
            if (cli == null) throw new FileNotFoundException("Local TypeScript compiler was not found. Install project dependencies first.");
            var arguments = new List<string> { cli };
            arguments.AddRange(tscArgs ?? []);
            if (!build) arguments.Add("--noEmit");
            arguments.AddRange(["--pretty", "false"]);
            var execution = await NativeProcessRunner.RunAsync("node", arguments, workingDirectory, timeoutSeconds,
                new Dictionary<string, string> { ["FORCE_COLOR"] = "0", ["NO_COLOR"] = "1" }, token);
            var baselineKey = workingDirectory + "\n" + string.Join("\n", tscArgs ?? []);
            TscBaselines.TryGetValue(baselineKey, out var previous);
            var report = TscReportParser.Parse(execution.FullLogPath, workingDirectory, execution.ExitCode, onlyNew ? previous : null, out var current);
            if (execution.Status == "completed" && current != null) UpdateBaseline(TscBaselines, baselineKey, current);
            return await CombineAsync("run_tsc", report, execution, workingDirectory, contextLines,
                trimOutput: (output, result) => TscReportParser.WithoutListed(output, result, workingDirectory, onlyNew ? previous : null, compactResponse), compactResponse: compactResponse);
        }, cancellationToken);

    [McpServerTool(Name = "run_cargo_check")]
    [Description("Run cargo check and return structured Rust compiler diagnostics with counts, file, line, column, code, severity, and message. Dependencies are not installed automatically.")]
    public Task<string> RunCargoCheckAsync(string workingDirectory,
        [Description("Arguments passed to cargo check, with each argument as a separate array element.")] string[]? cargoArgs = null,
        [Description("When true, list only diagnostics absent from the previous run with the same workingDirectory and cargoArgs in this server process, and add newCount and fixedCount.")] bool onlyNew = false,
        int timeoutSeconds = 180, [Description(ContextLinesText)] int contextLines = 0, [Description(CompactResponseText)] bool compactResponse = true,
        CancellationToken cancellationToken = default) =>
        RunAsync("run_cargo_check", workingDirectory, timeoutSeconds, async (_, token) =>
        {
            RejectManagedArguments(cargoArgs, "--message-format", "--color");
            var arguments = new List<string> { "check" };
            arguments.AddRange(cargoArgs ?? []);
            arguments.AddRange(["--message-format=json", "--color=never", "--quiet"]);
            var execution = await NativeProcessRunner.RunAsync("cargo", arguments, workingDirectory, timeoutSeconds, null, token, separateStdoutLog: true);
            try
            {
                var baselineKey = workingDirectory + "\n" + string.Join("\n", cargoArgs ?? []);
                CargoCheckBaselines.TryGetValue(baselineKey, out var previous);
                var reportPath = execution.StdoutLogPath ?? execution.FullLogPath;
                var report = CargoCheckParser.Parse(reportPath, workingDirectory, execution.ExitCode, onlyNew ? previous : null, out var current);
                if (execution.Status == "completed" && current != null) UpdateBaseline(CargoCheckBaselines, baselineKey, current);
                return await CombineAsync("run_cargo_check", report, execution, workingDirectory, contextLines,
                    trimOutput: (output, _) => compactResponse ? CargoCheckParser.WithoutCompilerMessages(output) : output, compactResponse: compactResponse);
            }
            finally
            {
                if (execution.StdoutLogPath != null)
                    try { File.Delete(execution.StdoutLogPath); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }, cancellationToken);

    [McpServerTool(Name = "run_eslint")]
    [Description("Run a locally installed ESLint and return error and warning counts, per-rule counts and the first 15 problems (errors first) from its JSON report. Dependencies are not installed automatically.")]
    public Task<string> RunEslintAsync(string workingDirectory,
        [Description("Arguments passed to eslint, with each argument as a separate array element, for example src or --quiet. Defaults to the current directory.") ] string[]? eslintArgs = null,
        [Description("When true, list only diagnostics absent from the previous run with the same workingDirectory and eslintArgs in this server process, and add newCount and fixedCount.")] bool onlyNew = false,
        int timeoutSeconds = 180, [Description(ContextLinesText)] int contextLines = 0, [Description(CompactResponseText)] bool compactResponse = true, CancellationToken cancellationToken = default) =>
        RunAsync("run_eslint", workingDirectory, timeoutSeconds, async (directory, token) =>
        {
            RejectManagedArguments(eslintArgs, "--format", "-f", "--output-file", "-o");
            var cli = FindPackageFile(workingDirectory, ["eslint"], Path.Combine("bin", "eslint.js"));
            if (cli == null) throw new FileNotFoundException("Local ESLint CLI was not found. Install project dependencies first.");
            var reportPath = Path.Combine(directory, "eslint.json");
            var arguments = new List<string> { cli };
            arguments.AddRange(eslintArgs is { Length: > 0 } ? eslintArgs : ["."]);
            arguments.AddRange(["--format", "json", "--output-file", reportPath]);
            var execution = await NativeProcessRunner.RunAsync("node", arguments, workingDirectory, timeoutSeconds,
                new Dictionary<string, string> { ["FORCE_COLOR"] = "0", ["NO_COLOR"] = "1" }, token);
            var baselineKey = workingDirectory + "\n" + string.Join("\n", eslintArgs ?? []);
            EslintBaselines.TryGetValue(baselineKey, out var previous);
            var parsed = await EslintReportParser.ParseAsync(reportPath, workingDirectory, execution.ExitCode, token, onlyNew ? previous : null);
            if (execution.Status == "completed" && parsed.Baseline != null) UpdateBaseline(EslintBaselines, baselineKey, parsed.Baseline);
            return await CombineAsync("run_eslint", parsed.Report, execution, workingDirectory, contextLines, compactResponse: compactResponse);
        }, cancellationToken);

    [McpServerTool(Name = "run_affected_tests")]
    [Description("Select test files related to Git changes by direct changes, naming conventions, and relative imports, then run each detected test runner once. Untracked files are included. Only installed project dependencies are used.")]
    public async Task<string> RunAffectedTestsAsync(string workingDirectory,
        [Description("Optional absolute path to the Python executable used for selected pytest files.")] string? pythonPath = null,
        int timeoutSeconds = 180, [Description(ContextLinesText)] int contextLines = 0, [Description(CompactResponseText)] bool compactResponse = true,
        CancellationToken cancellationToken = default)
    {
        if (!Path.IsPathFullyQualified(workingDirectory) || !Directory.Exists(workingDirectory))
        {
            UsageLog.RecordCall("run_affected_tests");
            return JsonDefaults.Serialize(new { status = "error", message = "workingDirectory must be an existing absolute directory." });
        }

        try
        {
            UsageLog.RecordCall("run_affected_tests");
            var (root, changedFiles, selected) = await AffectedTestSelector.SelectAsync(workingDirectory, cancellationToken);
            var groups = selected.GroupBy(item => new { item.Runner, item.ProjectDirectory });
            var runs = new JsonArray();
            var failed = false;
            var allNoTests = selected.Count > 0;
            foreach (var group in groups)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var files = group.Select(item => AsTestArgument(Path.GetRelativePath(group.Key.ProjectDirectory, Path.Combine(root, item.File.Replace('/', Path.DirectorySeparatorChar))).Replace(Path.DirectorySeparatorChar, '/'))).ToArray();
                var resultJson = group.Key.Runner switch
                {
                    "vitest" => await RunVitestAsync(group.Key.ProjectDirectory, files, timeoutSeconds, contextLines, compactResponse, cancellationToken),
                    "playwright" => await RunPlaywrightAsync(group.Key.ProjectDirectory, files, timeoutSeconds, contextLines, compactResponse, cancellationToken),
                    "bun" => await RunBunTestAsync(group.Key.ProjectDirectory, files, timeoutSeconds, contextLines, compactResponse, cancellationToken),
                    "pytest" => await RunPytestAsync(group.Key.ProjectDirectory, files, pythonPath, timeoutSeconds, contextLines, compactResponse, cancellationToken),
                    "dotnet" => await RunAffectedDotnetAsync(group.Key.ProjectDirectory, files, timeoutSeconds, contextLines, compactResponse, cancellationToken),
                    _ => JsonDefaults.Serialize(new { status = "error", message = $"Unsupported runner: {group.Key.Runner}" })
                };
                var result = JsonNode.Parse(resultJson)!.AsObject();
                var runStatus = result["status"]?.GetValue<string>();
                var noTests = runStatus == "no_tests";
                allNoTests &= noTests;
                failed |= !noTests && (runStatus is "error" or "cancelled" or "timeout" or "report_unavailable"
                    || (result["exitCode"]?.GetValue<int>() ?? 0) != 0);
                var run = new JsonObject
                {
                    ["runner"] = group.Key.Runner,
                    ["projectDirectory"] = Path.GetRelativePath(root, group.Key.ProjectDirectory).Replace(Path.DirectorySeparatorChar, '/'),
                    ["tests"] = new JsonArray(files.Select(file => (JsonNode?)JsonValue.Create(file)).ToArray()),
                    ["result"] = result
                };
                runs.Add(run);
            }

            return JsonDefaults.Serialize(new
            {
                status = selected.Count == 0 || allNoTests ? "no_tests" : failed ? "error" : "success",
                repositoryRoot = root,
                changedFiles,
                selectedTests = selected.Select(item => new { file = item.File, runner = item.Runner }),
                runs
            });
        }
        catch (OperationCanceledException)
        {
            return JsonDefaults.Serialize(new { status = "cancelled" });
        }
        catch (Exception exception) when (exception is IOException or ArgumentException or System.ComponentModel.Win32Exception)
        {
            return JsonDefaults.Serialize(new { status = "error", message = exception.Message });
        }
    }

    private Task<string> RunAffectedDotnetAsync(string projectDirectory, string[] files, int timeoutSeconds, int contextLines,
        bool compactResponse, CancellationToken cancellationToken)
    {
        var project = Directory.GetFiles(projectDirectory, "*.csproj").Order(StringComparer.Ordinal).FirstOrDefault()
            ?? throw new FileNotFoundException($"No .csproj was found for the selected .NET tests in {projectDirectory}.");
        var filters = files.Select(file => "FullyQualifiedName~" + EscapeDotnetFilterValue(Path.GetFileNameWithoutExtension(file))).Distinct(StringComparer.Ordinal);
        var arguments = new List<string> { project };
        arguments.AddRange(["--filter", string.Join('|', filters)]);
        return RunDotnetTestAsync(projectDirectory, arguments.ToArray(), timeoutSeconds, contextLines, compactResponse, cancellationToken);
    }

    private static string AsTestArgument(string path) => path.StartsWith("-", StringComparison.Ordinal) ? "./" + path : path;

    private static string EscapeDotnetFilterValue(string value) => System.Text.RegularExpressions.Regex.Replace(value, @"[\\&|=!~]", @"\$0");

    [McpServerTool(Name = "inspect_playwright_trace")]
    [Description("Inspect a trace ZIP without extracting it and return failed actions, HTTP and JavaScript errors, and a DOM excerpt before the failure. This does not replace visual verification of rendering or CSS.")]
    public Task<string> InspectPlaywrightTraceAsync(string tracePath, int maxOutputChars = 6000, string? workingDirectory = null,
        CancellationToken cancellationToken = default) =>
        UsageLog.TrackCallAsync("inspect_playwright_trace", PlaywrightTraceParser.ParseAsync(tracePath, Math.Clamp(maxOutputChars, 1000, 50000), cancellationToken, workingDirectory));

    private async Task<string> RunAsync(string tool, string workingDirectory, int timeoutSeconds,
        Func<string, CancellationToken, Task<string>> action, CancellationToken cancellationToken)
    {
        if (!Path.IsPathFullyQualified(workingDirectory) || !Directory.Exists(workingDirectory))
        {
            UsageLog.RecordCall(tool);
            return JsonDefaults.Serialize(new { status = "error", message = "workingDirectory must be an existing absolute directory." });
        }
        var directory = Path.Combine(Path.GetTempPath(), $"inazuma-diagnostics-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.ApplicationStopping);
        try { return await action(directory, linked.Token); }
        catch (OperationCanceledException) { UsageLog.RecordCall(tool); return JsonDefaults.Serialize(new { status = "cancelled" }); }
        catch (Exception exception) when (exception is IOException or ArgumentException or System.ComponentModel.Win32Exception)
        {
            UsageLog.RecordCall(tool);
            return JsonDefaults.Serialize(new { status = "error", message = exception.Message });
        }
        finally { await DeleteDiagnosticDirectoryAsync(directory); }
    }

    private static async Task DeleteDiagnosticDirectoryAsync(string directory)
    {
        for (var attempt = 1; attempt <= 5; attempt++)
        {
            if (!Directory.Exists(directory)) return;
            try
            {
                Directory.Delete(directory, recursive: true);
                return;
            }
            catch (IOException) when (attempt < 5)
            {
                await Task.Delay(attempt * 50);
            }
            catch (UnauthorizedAccessException) when (attempt < 5)
            {
                await Task.Delay(attempt * 50);
            }
        }
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
            var venvPython = OperatingSystem.IsWindows()
                ? Path.Combine(directory.FullName, ".venv", "Scripts", "python.exe")
                : Path.Combine(directory.FullName, ".venv", "bin", "python");
            if (File.Exists(venvPython)) return venvPython;
        }
        return "python";
    }

    private static void UpdateBaseline(System.Collections.Concurrent.ConcurrentDictionary<string, HashSet<string>> baselines,
        string key, HashSet<string> current)
    {
        if (baselines.Count >= 20 && !baselines.ContainsKey(key)) baselines.Clear();
        baselines[key] = current;
    }

    private static bool UsesTestingPlatform(string workingDirectory)
    {
        for (var directory = new DirectoryInfo(workingDirectory); directory != null; directory = directory.Parent)
        {
            var file = Path.Combine(directory.FullName, "global.json");
            if (!File.Exists(file)) continue;
            try
            {
                var options = new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };
                using var document = JsonDocument.Parse(File.ReadAllText(file), options);
                return document.RootElement.ValueKind == JsonValueKind.Object
                    && document.RootElement.TryGetProperty("test", out var test) && test.ValueKind == JsonValueKind.Object
                    && test.TryGetProperty("runner", out var runner) && runner.ValueKind == JsonValueKind.String
                    && runner.GetString() == "Microsoft.Testing.Platform";
            }
            catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException) { return false; }
        }
        return false;
    }

    private static bool HasFailures(JsonObject result) => result["failures"] is JsonArray { Count: > 0 };

    private static bool ListsEveryFailure(JsonObject result, out List<JsonObject> failures)
    {
        failures = result["failures"] is JsonArray array ? array.OfType<JsonObject>().ToList() : [];
        return failures.Count > 0 && result["globalErrors"] is not JsonArray { Count: > 0 }
            && result["stats"]?["failed"]?.GetValue<int>() <= failures.Sum(failure => failure["count"]?.GetValue<int>() ?? 1);
    }

    private static string[]? FailedFiles(JsonObject result)
    {
        if (!ListsEveryFailure(result, out var failures)) return null;
        var files = failures.Select(failure => failure["file"]?.GetValue<string>() ?? "").Distinct().ToArray();
        return files.Any(file => file.Length == 0 || file.EndsWith("...", StringComparison.Ordinal)) ? null : files;
    }

    private static string[]? DotnetFilter(JsonObject result)
    {
        if (!ListsEveryFailure(result, out var failures)) return null;
        var names = failures.Select(failure => (failure["title"]?.GetValue<string>() ?? "").Split('(')[0].Trim()).Distinct().ToArray();
        if (names.Any(name => name.Length == 0 || name.EndsWith("...", StringComparison.Ordinal))) return null;
        var escaped = names.Select(name => "FullyQualifiedName~" + System.Text.RegularExpressions.Regex.Replace(name, @"[\\&|=!~]", @"\$0"));
        return ["--filter", string.Join("|", escaped)];
    }

    private static async Task<string> CombineAsync(string tool, string report, NativeProcessRunner.Result execution, string workingDirectory,
        int contextLines, Func<JsonObject, string[]?>? rerun = null, Func<string, JsonObject, string>? trimOutput = null, bool compactResponse = true, bool includeSuccessOutput = false)
    {
        var json = await CombineResultAsync(tool, report, execution, workingDirectory, contextLines, rerun, trimOutput, compactResponse, includeSuccessOutput);
        long sourceBytes = 0;
        try { sourceBytes = new FileInfo(execution.FullLogPath).Length; } catch (IOException) { }
        UsageLog.Record(tool, sourceBytes, json);
        return json;
    }

    private static async Task<string> CombineResultAsync(string tool, string report, NativeProcessRunner.Result execution, string workingDirectory, int contextLines,
        Func<JsonObject, string[]?>? rerun, Func<string, JsonObject, string>? trimOutput, bool compactResponse, bool includeSuccessOutput)
    {
        report = CodeContext.Add(report, workingDirectory, contextLines);
        var result = JsonNode.Parse(report)!.AsObject();
        if (execution.Status != "completed") result["status"] = execution.Status;
        result["fullLogPath"] = execution.FullLogPath;
        result["durationMs"] = execution.DurationMs;
        var testTool = tool is "run_bun_test" or "run_vitest" or "run_pytest" or "run_playwright" or "run_dotnet_test";
        if (result["status"]?.GetValue<string>() != "success" || includeSuccessOutput || testTool)
        {
            if (result["rerunArgs"] == null && rerun?.Invoke(result) is { Length: > 0 } rerunArgs)
                result["rerunArgs"] = new JsonArray(rerunArgs.Select(argument => (JsonNode?)JsonValue.Create(argument)).ToArray());
            var parsedTestReport = testTool && execution.Status == "completed" && result["globalErrors"] is JsonArray { Count: 0 }
                && (result["stats"]?["total"]?.GetValue<int>() > 0 || result["status"]?.GetValue<string>() == "no_tests");
            var tail = trimOutput == null ? execution.Tail : trimOutput(execution.Tail, result);
            if (compactResponse && trimOutput == null)
            {
                if (parsedTestReport) tail = RemoveRepresentedLines(CompactTestOutput(tool, tail), result);
                else if (!testTool) tail = RemoveRepresentedLines(tail, result);
            }
            var compressed = testTool && (!compactResponse || !parsedTestReport)
                ? tail : (await OutputCompressor.CompressAsync(tail, 1500)).Content;
            var lines = compressed.Split('\n');
            var signalStart = Array.FindIndex(lines, line => System.Text.RegularExpressions.Regex.IsMatch(line, @"^L\d+: "));
            if (signalStart >= 0)
            {
                var signalEnd = signalStart;
                while (signalEnd < lines.Length && System.Text.RegularExpressions.Regex.IsMatch(lines[signalEnd], @"^L\d+: ")) signalEnd++;
                Array.Sort(lines, signalStart, signalEnd - signalStart, Comparer<string>.Create((left, right) => int.Parse(left.AsSpan(1, left.IndexOf(':') - 1)).CompareTo(int.Parse(right.AsSpan(1, right.IndexOf(':') - 1)))));
                compressed = string.Join('\n', lines);
            }
            if (compressed.Length > 0 || (!includeSuccessOutput && !(compactResponse && parsedTestReport))) result["output"] = compressed;
        }

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

        json = DiagnosticBudget.Fit(result, 8000, "failures", "diagnostics", "errors", "warnings", "globalErrors");
        if (json.Length <= 8000) return json;

        foreach (var arrayName in new[] { "failures", "diagnostics", "errors", "warnings" })
            if (result[arrayName] is JsonArray { Count: > 0 } items && items[0] is JsonObject first)
                first.Remove("codeContext");

        json = result.ToJsonString(JsonDefaults.Options);
        if (json.Length <= 8000) return json;

        if (result["failures"] is JsonArray { Count: > 0 } failures && failures[0] is JsonObject primaryFailure
            && primaryFailure["attachments"] is JsonArray attachments)
        {
            primaryFailure["omittedAttachments"] = attachments.Count;
            primaryFailure.Remove("attachments");
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

    private static string CompactTestOutput(string tool, string output)
    {
        var kept = new List<string>();
        var dropping = false;
        var lines = output.Replace("\r\n", "\n").Split('\n');
        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index];
            var text = PlaywrightResultParser.StripAnsi(line).Trim();
            if (text.Length == 0) continue;
            bool Match(string pattern) => System.Text.RegularExpressions.Regex.IsMatch(text, pattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (tool == "run_pytest")
            {
                if (Match(@"^=+\s*(FAILURES|short test summary info)\s*=+$")) { dropping = true; continue; }
                if (Match(@"^=+\s*ERRORS\s*=+$") && lines.Skip(index + 1).TakeWhile(next => !next.TrimStart().StartsWith("====", StringComparison.Ordinal)).Any(next => System.Text.RegularExpressions.Regex.IsMatch(next, @"^_+\s*ERROR at (setup|teardown)\s", System.Text.RegularExpressions.RegexOptions.IgnoreCase))) { dropping = true; continue; }
                if (Match(@"^=+\s*warnings summary\s*=+$")) dropping = false;
                if (Match(@"^=+.*\b(?:failed|passed|skipped|errors?|warnings?)\b.*\bin\s+[\d.]+s.*=+$")) dropping = false;
                if (text.Contains("generated xml file:", StringComparison.OrdinalIgnoreCase) || Match(@"^\S+\.py\s+[.FsExX]+\s*(?:\[\s*\d+%\])?$")) continue;
            }
            else if (tool == "run_vitest")
            {
                if (text.Contains("Failed Tests", StringComparison.Ordinal) && Match(@"^[⎯─-]+")) { dropping = true; continue; }
                if (Match(@"^(Test Files|Tests|Start at|Duration)\s+")) dropping = false;
                if (Match(@"^[⎯─-]+\s*(Unhandled Errors|Failed Suites)\b")) dropping = false;
                if (text.StartsWith("JSON report written to ", StringComparison.OrdinalIgnoreCase) || Match(@"^[❯✓×↓]\s") || Match(@"^RUN\s+v\d")) continue;
            }
            else if (tool == "run_bun_test")
            {
                if (Match(@"^\d+\s*\|") && lines.Skip(index + 1).Any(next => MatchBunFailure(next))) dropping = true;
                if (MatchBunFailure(line)) { dropping = false; continue; }
                if (Match(@"^\((pass|skip|todo)\)\s") || Match(@"^bun test v\d") || Match(@"^\S+\.(?:test|spec)\.[^\s:]+:$")) continue;
            }
            else if (tool == "run_playwright")
            {
                if (Match(@"^\d+\)\s+.*:\d+:\d+\s")) { dropping = true; continue; }
                if (Match(@"^\d+\s+(failed|passed|skipped|flaky)(?:\s|$)")) dropping = false;
                if (Match(@"^[.·FEs×✓-]+$") || Match(@"^Running \d+ tests? using ") || Match(@"^\S+.*:\d+:\d+\s+›")) continue;
            }
            else if (tool == "run_dotnet_test")
            {
                if (Match(@"^(Passed|Failed|Skipped)\s+.+\[<?\s*[\d.,]+\s*(?:ms|s)\]$")) { dropping = true; continue; }
                if (Match(@"^(Failed!|Passed!|Test Run (Failed|Successful))") || text.StartsWith("Results File:", StringComparison.OrdinalIgnoreCase)) dropping = false;
                if (text.StartsWith("Results File:", StringComparison.OrdinalIgnoreCase) || Match(@"^\[.*\]\s+.*\[(PASS|FAIL|SKIP)\]$")) continue;
            }
            if (dropping) continue;
            kept.Add(line);
        }
        return string.Join('\n', kept);
    }

    private static bool MatchBunFailure(string line) =>
        PlaywrightResultParser.StripAnsi(line).TrimStart().StartsWith("(fail) ", StringComparison.OrdinalIgnoreCase);

    private static string RemoveRepresentedLines(string output, JsonObject result)
    {
        var represented = new HashSet<string>(StringComparer.Ordinal);
        void AddText(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            var lines = value.Replace("\r\n", "\n").Split('\n');
            foreach (var line in value.EndsWith("...", StringComparison.Ordinal) ? lines.SkipLast(1) : lines)
                if (!string.IsNullOrWhiteSpace(line)) represented.Add(line.Trim());
        }
        if (result["globalErrors"] is JsonArray globals)
            foreach (var value in globals) AddText(value?.GetValue<string>());
        foreach (var arrayName in new[] { "failures", "errors", "warnings", "diagnostics" })
            if (result[arrayName] is JsonArray items)
                foreach (var item in items.OfType<JsonObject>())
                {
                    AddText(item["message"]?.GetValue<string>());
                    if (item["errors"] is JsonArray errors)
                        foreach (var error in errors.OfType<JsonObject>()) { AddText(error["message"]?.GetValue<string>()); AddText(error["stack"]?.GetValue<string>()); }
                }
        return string.Join('\n', output.Replace("\r\n", "\n").Split('\n').Where(line =>
        {
            var text = PlaywrightResultParser.StripAnsi(line).Trim();
            var withoutPrefix = System.Text.RegularExpressions.Regex.Replace(text, @"^(?:E\s+|error:\s*)", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (represented.Contains(text) || represented.Contains(withoutPrefix)) return false;
            if (result["failures"] is JsonArray failures)
            {
                foreach (var failure in failures.OfType<JsonObject>())
                {
                    var title = failure["title"]?.GetValue<string>();
                    if (string.IsNullOrEmpty(title) || title.EndsWith("...", StringComparison.Ordinal)) continue;
                    if ((text.Contains("[FAIL]", StringComparison.OrdinalIgnoreCase) || text.StartsWith("Failed ", StringComparison.Ordinal)) && text.Contains(title, StringComparison.Ordinal)) return false;
                }
                if (failures.Count > 0 && System.Text.RegularExpressions.Regex.IsMatch(text, @"^at\s+(?:System\.Reflection\.|System\.RuntimeMethodHandle\.|Xunit\.)", System.Text.RegularExpressions.RegexOptions.IgnoreCase)) return false;
            }
            foreach (var arrayName in new[] { "errors", "warnings", "diagnostics" })
                if (result[arrayName] is JsonArray items)
                    foreach (var item in items.OfType<JsonObject>())
                    {
                        var code = (item["ruleId"] ?? item["code"])?.GetValue<string>();
                        var message = item["message"]?.GetValue<string>();
                        if (!string.IsNullOrEmpty(code) && !string.IsNullOrEmpty(message) && !message.EndsWith("...", StringComparison.Ordinal) && text.Contains(code, StringComparison.Ordinal) && text.Contains(message, StringComparison.Ordinal)) return false;
                    }
            return true;
        })).Trim('\n');
    }
}