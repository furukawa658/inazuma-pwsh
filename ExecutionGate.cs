using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using ModelContextProtocol.Server;

namespace InazumaPwsh;

public sealed class ExecutionGate
{
    private sealed record FailureEntry(long Timestamp, string CommandKey, string Signature, string DiffFingerprint);

    private const string DefaultMode = "manual";
    private const int MaxHistory = 64;
    private readonly ConcurrentDictionary<string, FailureEntry[]> _history = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _tripped = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();
    private string _mode = NormalizeMode(Environment.GetEnvironmentVariable("ZUMA_EXECUTION_GATE_MODE"));
    private bool _enabled = ParseBoolean(Environment.GetEnvironmentVariable("ZUMA_EXECUTION_GATE_ENABLED"), true);
    private int _threshold = ParseInteger(Environment.GetEnvironmentVariable("ZUMA_EXECUTION_GATE_THRESHOLD"), 3, 2, 10);
    private int _historySeconds = ParseInteger(Environment.GetEnvironmentVariable("ZUMA_EXECUTION_GATE_HISTORY_SECONDS"), 120, 10, 3600);
    private bool _skipRedPhase = ParseBoolean(Environment.GetEnvironmentVariable("ZUMA_EXECUTION_GATE_SKIP_RED_PHASE"), false);

    public ExecutionGateSnapshot Snapshot() => new(_enabled, _mode, _threshold, _historySeconds, _skipRedPhase,
        _tripped.Select(entry => new ExecutionGateTrip(entry.Key, entry.Value)).OrderBy(entry => entry.Key, StringComparer.Ordinal).ToArray());

    public ExecutionGateSnapshot Set(string mode, bool? enabled, int? threshold, int? historySeconds, bool? skipRedPhase)
    {
        _mode = NormalizeMode(mode);
        if (enabled.HasValue) _enabled = enabled.Value;
        if (threshold.HasValue) _threshold = Math.Clamp(threshold.Value, 2, 10);
        if (historySeconds.HasValue) _historySeconds = Math.Clamp(historySeconds.Value, 10, 3600);
        if (skipRedPhase.HasValue) _skipRedPhase = skipRedPhase.Value;
        PersistState();
        return Snapshot();
    }

    public ExecutionGateReset Reset(string? workspaceKey = null)
    {
        if (workspaceKey == null)
        {
            _history.Clear();
            _tripped.Clear();
        }
        else
        {
            foreach (var key in _history.Keys.Where(key => key.StartsWith(workspaceKey + "\n", StringComparison.OrdinalIgnoreCase)))
                _history.TryRemove(key, out _);
            foreach (var key in _tripped.Keys.Where(key => key.StartsWith(workspaceKey + "\n", StringComparison.OrdinalIgnoreCase)))
                _tripped.TryRemove(key, out _);
        }
        PersistState();
        return new ExecutionGateReset("success", workspaceKey != null, Snapshot());
    }

    public async Task<ExecutionGateDecision> EvaluateAsync(string tool, string workingDirectory, string commandKey,
        bool testRun, bool hasSpecificTarget, string? report = null, CancellationToken cancellationToken = default)
    {
        if (!_enabled) return ExecutionGateDecision.Allow();
        var workspaceKey = await ResolveRepositoryRootAsync(workingDirectory, cancellationToken);
        var normalizedCommand = NormalizeCommand(commandKey);
        var scopeKey = BuildScopeKey(workspaceKey, normalizedCommand);
        FailureEntry? trippedEntry = null;
        var wasTripped = false;
        lock (_gate)
        {
            if (_tripped.ContainsKey(scopeKey))
            {
                wasTripped = true;
                var cutoff = DateTimeOffset.UtcNow.AddSeconds(-_historySeconds).ToUnixTimeMilliseconds();
                trippedEntry = _history.TryGetValue(scopeKey, out var entries)
                    ? entries.LastOrDefault(entry => entry.Timestamp >= cutoff)
                    : null;
            }
        }
        if (wasTripped && trippedEntry == null)
        {
            _tripped.TryRemove(scopeKey, out _);
            _history.TryRemove(scopeKey, out _);
            PersistState();
        }
        else if (trippedEntry != null)
        {
            var currentDiff = await DiffFingerprintAsync(workspaceKey, cancellationToken);
            if (trippedEntry.DiffFingerprint != currentDiff)
            {
                _tripped.TryRemove(scopeKey, out _);
                _history.TryRemove(scopeKey, out _);
                PersistState();
            }
            else
            {
                return ExecutionGateDecision.Block("circuit_breaker", $"[Zuma Circuit Breaker] Execution is locked after {_threshold} identical failures. Reset the execution gate or change the command or Git diff.",
                    new { mode = _mode, threshold = _threshold, workspace = workspaceKey, tool });
            }
        }

        if (testRun && _mode == "incremental" && !hasSpecificTarget)
        {
            return ExecutionGateDecision.Block("incremental_test_scope", "[Zuma Execution Gate] Full test suite execution is disabled in incremental mode. Run run_affected_tests, provide a specific target, or switch to milestone mode.",
                new { mode = _mode, workspace = workspaceKey, tool });
        }

        if (testRun && _skipRedPhase && await HasOnlyTestChangesAsync(workspaceKey, cancellationToken))
        {
            return ExecutionGateDecision.Block("red_phase", "[Zuma Execution Gate] Only test files changed. Red-phase pre-implementation execution is disabled. Change the implementation first, or reset skipRedPhase.",
                new { mode = _mode, workspace = workspaceKey, tool });
        }

        if (report == null) return ExecutionGateDecision.Allow();
        var signature = ExtractFailureSignature(report);
        if (signature == null) return Clear(this, scopeKey, commandKey, workspaceKey);
        var diffFingerprint = await DiffFingerprintAsync(workspaceKey, cancellationToken);
        var entry = new FailureEntry(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), normalizedCommand, signature, diffFingerprint);
        lock (_gate)
        {
            var cutoff = DateTimeOffset.UtcNow.AddSeconds(-_historySeconds).ToUnixTimeMilliseconds();
            var current = _history.GetOrAdd(scopeKey, _ => []).Where(item => item.Timestamp >= cutoff).Append(entry).TakeLast(MaxHistory).ToArray();
            _history[scopeKey] = current;
            var matches = current.Reverse().TakeWhile(item => item.CommandKey == normalizedCommand && item.Signature == signature && item.DiffFingerprint == diffFingerprint).Count();
            if (matches >= _threshold)
                _tripped[scopeKey] = DateTimeOffset.UtcNow;
            if (matches >= _threshold) PersistState();
            return matches >= _threshold
                ? ExecutionGateDecision.Block("circuit_breaker", $"[Zuma Circuit Breaker] The same failure occurred {_threshold} times with the same command and Git diff. The next execution is locked. Analyze the root cause, change the approach, or reset the gate.",
                    new { mode = _mode, threshold = _threshold, consecutiveFailures = matches, workspace = workspaceKey, tool })
                : ExecutionGateDecision.Allow(new { consecutiveFailures = matches, threshold = _threshold, mode = _mode });
        }
    }

    private static ExecutionGateDecision Clear(ExecutionGate gate, string scopeKey, string commandKey, string workspaceKey)
    {
        gate._tripped.TryRemove(scopeKey, out _);
        gate._history.TryRemove(scopeKey, out _);
        gate.PersistState();
        return ExecutionGateDecision.Allow(new { consecutiveFailures = 0, threshold = gate._threshold, mode = gate._mode, workspace = workspaceKey, command = NormalizeCommand(commandKey) });
    }

    public static bool IsTestCommand(string script) => Regex.IsMatch(script,
        @"(?im)(?:^|[;&|\s])(?:dotnet\s+test|pytest\b|py(?:thon(?:\.exe)?)?\s+-m\s+pytest|bun\s+test\b|(?:npx\s+)?(?:vitest\s+run|vitest\b|playwright\s+test)\b|cargo\s+test\b)", RegexOptions.Compiled);

    public static bool HasSpecificTestTarget(string script)
    {
        if (!IsTestCommand(script)) return true;
        return Regex.IsMatch(script, @"(?i)(?:--filter(?:=|\s)|--test-name-pattern(?:=|\s)|--grep(?:=|\s)|\s-g(?:=|\s)|\s-k(?:\s|=)|\s-m\s+|::|\.(?:test|spec)\.[cm]?[jt]sx?(?:\s|$)|\.py(?:\s|$)|tests?[\\/][^\s;|]+)", RegexOptions.Compiled);
    }

    public static bool HasSpecificTestArguments(string tool, string[]? arguments)
    {
        if (arguments is not { Length: > 0 }) return false;
        if (tool == "run_dotnet_test") return arguments.Any(argument => argument.StartsWith("--filter", StringComparison.Ordinal));
        var optionsWithValues = new HashSet<string>(StringComparer.Ordinal)
        {
            "--timeout", "--test-timeout", "--max-workers", "--workers", "--retries", "--config", "--coverage-reporter", "--reporter-outfile"
        };
        var scopeOptions = new HashSet<string>(StringComparer.Ordinal)
        {
            "--test-name-pattern", "--grep", "-g", "-k", "-m", "-t", "--project", "--browser"
        };
        for (var index = 0; index < arguments.Length; index++)
        {
            var argument = arguments[index];
            if (scopeOptions.Any(option => argument.StartsWith(option + "=", StringComparison.Ordinal) || argument == option)) return true;
            if (optionsWithValues.Contains(argument)) { index++; continue; }
            if (!argument.StartsWith("-", StringComparison.Ordinal)) return true;
        }
        return false;
    }

    public static string ApplyMetadata(string report, ExecutionGateDecision decision)
    {
        if (decision.Allowed) return report;
        try
        {
            if (JsonNode.Parse(report)?.AsObject() is not { } root) return report;
            root["executionGate"] = JsonSerializer.SerializeToNode(new
            {
                tripped = !decision.Allowed,
                reason = decision.Reason,
                message = decision.Message,
                details = decision.Details
            }, JsonDefaults.Options);
            var json = root.ToJsonString(JsonDefaults.Options);
            if (json.Length > 8000) root.Remove("output");
            return DiagnosticBudget.Fit(root, 8000, "failures", "diagnostics", "errors", "warnings", "globalErrors");
        }
        catch (JsonException)
        {
            return report;
        }
    }

    private static Task<string> ResolveRepositoryRootAsync(string directory, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(FindRepositoryRoot(directory) ?? Path.GetFullPath(directory));
    }

    private static string? FindRepositoryRoot(string directory)
    {
        for (var current = new DirectoryInfo(Path.GetFullPath(directory)); current != null; current = current.Parent)
            if (Directory.Exists(Path.Combine(current.FullName, ".git")) || File.Exists(Path.Combine(current.FullName, ".git")))
                return current.FullName;
        return null;
    }

    private static async Task<string> DiffFingerprintAsync(string root, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(Path.Combine(root, ".git")) && !File.Exists(Path.Combine(root, ".git")))
            return await DirectoryFingerprintAsync(root, cancellationToken);
        var diff = await RunGitAsync(root, "diff --binary HEAD --", cancellationToken);
        var untracked = await RunGitAsync(root, "ls-files --others --exclude-standard -z", cancellationToken);
        var changed = await RunGitAsync(root, "status --porcelain=v1 -z", cancellationToken);
        if (diff.ExitCode != 0 || untracked.ExitCode != 0 || changed.ExitCode != 0)
            return await DirectoryFingerprintAsync(root, cancellationToken);
        using var hash = SHA256.Create();
        hash.ComputeHash(Encoding.UTF8.GetBytes(diff.Output + "\n" + changed.Output));
        var state = new MemoryStream();
        state.Write(hash.Hash ?? []);
        state.Write(Encoding.UTF8.GetBytes(untracked.Output));
        foreach (var relative in untracked.Output.Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            var path = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
            try
            {
                var info = new FileInfo(path);
                state.Write(Encoding.UTF8.GetBytes($"\n{relative}\n{info.Length}\n{info.LastWriteTimeUtc.Ticks}\n"));
                if (info.Exists && info.Length <= 2 * 1024 * 1024)
                    state.Write(await File.ReadAllBytesAsync(path, cancellationToken));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
            {
                state.Write(Encoding.UTF8.GetBytes($"\n{relative}\n<unreadable>"));
            }
        }
        return Convert.ToHexString(SHA256.HashData(state.ToArray()));
    }

    private static Task<string> DirectoryFingerprintAsync(string root, CancellationToken cancellationToken)
    {
        return Task.Run(() =>
        {
            using var state = new MemoryStream();
            var rootPath = Path.GetFullPath(root);
            var files = 0;
            long hashedBytes = 0;
            var ignored = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                ".git", "node_modules", "bin", "obj", "dist", "coverage", ".venv", "target", "__pycache__", ".pytest_cache"
            };
            var stack = new Stack<string>();
            stack.Push(rootPath);
            while (stack.Count > 0 && files < 1000)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var directory = stack.Pop();
                try
                {
                    foreach (var child in Directory.EnumerateDirectories(directory))
                    {
                        var name = Path.GetFileName(child);
                        if (!ignored.Contains(name)) stack.Push(child);
                    }
                    foreach (var file in Directory.EnumerateFiles(directory))
                    {
                        var info = new FileInfo(file);
                        var relative = Path.GetRelativePath(rootPath, file).Replace('\\', '/');
                        state.Write(Encoding.UTF8.GetBytes($"{relative}\n{info.Length}\n{info.LastWriteTimeUtc.Ticks}\n"));
                        if (info.Length <= 64 * 1024 && hashedBytes + info.Length <= 4 * 1024 * 1024)
                        {
                            var content = File.ReadAllBytes(file);
                            state.Write(content);
                            hashedBytes += content.LongLength;
                        }
                        if (++files >= 1000) break;
                    }
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    state.Write(Encoding.UTF8.GetBytes($"<unreadable:{directory}>"));
                }
            }
            return Convert.ToHexString(SHA256.HashData(state.ToArray()));
        }, cancellationToken);
    }

    private static async Task<bool> HasOnlyTestChangesAsync(string root, CancellationToken cancellationToken)
    {
        var status = await RunGitAsync(root, "status --porcelain=v1 -z", cancellationToken);
        if (status.ExitCode != 0) return false;
        var paths = status.Output.Split('\0', StringSplitOptions.RemoveEmptyEntries)
            .Select(entry => entry.Length > 3 ? entry[3..] : entry).Where(path => path.Length > 0).ToArray();
        return paths.Length > 0 && paths.All(IsTestPath);
    }

    private static bool IsTestPath(string path)
    {
        var normalized = path.Replace('\\', '/');
        return normalized.Contains("/test/", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("/tests/", StringComparison.OrdinalIgnoreCase)
            || Regex.IsMatch(normalized, @"(?:^|/)(?:test_|.*(?:\.test|\.spec|_test|Tests?))", RegexOptions.IgnoreCase);
    }

    private static async Task<(int ExitCode, string Output)> RunGitAsync(string directory, string arguments, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo("git")
        {
            WorkingDirectory = directory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var argument in arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries)) startInfo.ArgumentList.Add(argument);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        Process? process = null;
        try
        {
            process = Process.Start(startInfo);
            if (process == null) return (-1, "");
            process.StandardInput.Close();
            var stdoutTask = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var stderrTask = process.StandardError.ReadToEndAsync(timeout.Token);
            var exitTask = process.WaitForExitAsync(timeout.Token);
            await Task.WhenAll(stdoutTask, stderrTask, exitTask);
            return (process.ExitCode, process.ExitCode == 0 ? stdoutTask.Result : stderrTask.Result);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            try { process?.Kill(entireProcessTree: true); } catch { }
            return (-1, "");
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or IOException)
        {
            return (-1, "");
        }
        finally
        {
            process?.Dispose();
        }
    }

    private static string? ExtractFailureSignature(string report)
    {
        try
        {
            var root = JsonNode.Parse(report)?.AsObject();
            if (root == null) return null;
            var status = root["status"]?.GetValue<string>();
            if (status is null or "success" or "no_tests") return null;
            foreach (var key in new[] { "failures", "diagnostics", "errors", "globalErrors" })
            {
                if (root[key] is JsonArray { Count: > 0 } items)
                {
                    var item = items[0];
                    string code, message, file, line;
                    if (item is JsonObject itemObject)
                    {
                        code = itemObject["code"]?.GetValue<string>() ?? itemObject["ruleId"]?.GetValue<string>() ?? "";
                        message = itemObject["message"]?.GetValue<string>() ?? itemObject["title"]?.GetValue<string>() ?? itemObject.ToJsonString(JsonDefaults.Options);
                        file = itemObject["file"]?.GetValue<string>() ?? "";
                        line = itemObject["line"]?.ToString() ?? "";
                    }
                    else
                    {
                        code = "";
                        message = item?.GetValue<string>() ?? item?.ToJsonString(JsonDefaults.Options) ?? "";
                        file = "";
                        line = "";
                    }
                    return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(NormalizeError($"{code}|{message}|{file}|{line}"))));
                }
            }
            var fallback = root["output"]?.GetValue<string>() ?? report;
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(NormalizeError(fallback))));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string NormalizeError(string value) => Regex.Replace(Regex.Replace(value.Replace('\\', '/'), @"[A-Za-z]:/[^\s:]+", "<path>"), @"\b\d{10,}\b", "<id>");
    private static string NormalizeCommand(string value) => Regex.Replace(value.Replace("\r", "").Trim(), @"\s+", " ");
    private void PersistState()
    {
        try
        {
            var directory = Path.Combine(Path.GetTempPath(), "inazuma-pwsh-logs");
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "execution-gate.json");
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            var state = new
            {
                enabled = _enabled,
                mode = _mode,
                repeatFailureThreshold = _threshold,
                historySeconds = _historySeconds,
                skipRedPhase = _skipRedPhase,
                tripped = _tripped.Select(entry => new { key = entry.Key, trippedAt = entry.Value }).ToArray()
            };
            File.WriteAllText(temporary, JsonDefaults.Serialize(state));
            File.Move(temporary, path, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static string BuildScopeKey(string workspace, string command) => workspace + "\n" + command;
    private static string NormalizeMode(string? mode) => mode?.Trim().ToLowerInvariant() switch { "incremental" => "incremental", "milestone" => "milestone", _ => DefaultMode };
    private static bool ParseBoolean(string? value, bool fallback) => bool.TryParse(value, out var result) ? result : fallback;
    private static int ParseInteger(string? value, int fallback, int minimum, int maximum) => int.TryParse(value, out var result) ? Math.Clamp(result, minimum, maximum) : fallback;
}

public sealed record ExecutionGateSnapshot(
    [property: JsonPropertyName("enabled")] bool Enabled,
    [property: JsonPropertyName("mode")] string Mode,
    [property: JsonPropertyName("repeatFailureThreshold")] int RepeatFailureThreshold,
    [property: JsonPropertyName("historySeconds")] int HistorySeconds,
    [property: JsonPropertyName("skipRedPhase")] bool SkipRedPhase,
    [property: JsonPropertyName("tripped")] ExecutionGateTrip[] Tripped);
public sealed record ExecutionGateTrip(
    [property: JsonPropertyName("key")] string Key,
    [property: JsonPropertyName("trippedAt")] DateTimeOffset TrippedAt);
public sealed record ExecutionGateReset(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("scoped")] bool Scoped,
    [property: JsonPropertyName("snapshot")] ExecutionGateSnapshot Snapshot);
public sealed record ExecutionGateDecision(bool Allowed, string? Reason, string? Message, object? Details, bool HasMetadata)
{
    public static ExecutionGateDecision Allow(object? details = null) => new(true, null, null, details, details != null);
    public static ExecutionGateDecision Block(string reason, string message, object? details = null) => new(false, reason, message, details, details != null);
}

[McpServerToolType]
internal sealed class ExecutionGateTools(ExecutionGate gate)
{
    [McpServerTool(Name = "get_execution_gate")]
    [Description("Get Zuma execution-gate mode, repeat-failure threshold, and tripped circuit state.")]
    public string Get() => JsonDefaults.Serialize(gate.Snapshot());

    [McpServerTool(Name = "set_execution_gate")]
    [Description("Set Zuma execution-gate mode. Use manual to preserve existing behavior, incremental to restrict unscoped full test runs, or milestone to allow them.")]
    public string Set(string mode,
        [Description("Enable or disable circuit-breaker and full-suite policy checks.")] bool? enabled = null,
        [Description("Identical-failure count that trips the circuit breaker (2 to 10).")] int? repeatFailureThreshold = null,
        [Description("Seconds of recent failure history (10 to 3600).")] int? historySeconds = null,
        [Description("Warn-and-block test runs while only test files changed when true.")] bool? skipRedPhase = null) =>
        JsonDefaults.Serialize(gate.Set(mode, enabled, repeatFailureThreshold, historySeconds, skipRedPhase));

    [McpServerTool(Name = "reset_execution_gate")]
    [Description("Clear Zuma failure history and tripped circuit state. Optionally scope the reset to one workspace key.")]
    public string Reset(string? workspaceKey = null) => JsonDefaults.Serialize(gate.Reset(workspaceKey));
}
