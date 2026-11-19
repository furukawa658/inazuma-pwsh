using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace InazumaPwsh;

public static class PlaywrightTraceParser
{
    private const long MaxEntrySizeBytes = 20 * 1024 * 1024; // 20MB
    private const long MaxTotalSizeBytes = 80 * 1024 * 1024; // 80MB
    private const int MaxLineLength = 512 * 1024; // 512KB
    private const int MaxResourceBodyChars = 256;
    private const int MaxCollectionLimit = 50;
    private const int MaxProcessedLines = 50000;

    private static readonly Regex CredentialsRegex = new(
        @"(https?://[^:]+):([^@]+)@",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex SensitiveParamRegex = new(
        @"(?i)(password|token|secret|key|apikey|api_key|auth|access_token|refresh_token|credential|session)=([^&#\s]*)",
        RegexOptions.Compiled);

    private static readonly Regex AnsiRegex = new(
        @"\x1B(?:\[[0-?]*[ -/]*[@-~]|\][^\x07\x1B]*(?:\x07|\x1B\\)|[@-_])",
        RegexOptions.Compiled);

    private static readonly Regex PlaywrightFrameRegex = new(
        @"(?:^|[^a-zA-Z0-9_])node_modules/(?:@playwright/|playwright(?:-core)?/)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly HashSet<string> VoidElements = new(StringComparer.OrdinalIgnoreCase)
    {
        "area", "base", "br", "col", "embed", "hr", "img", "input", "link", "meta", "param", "source", "track", "wbr"
    };

    private static readonly HashSet<string> ExcludedTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "script", "style", "svg", "noscript"
    };

    public static async Task<string> ParseAsync(string tracePath, int maxOutputChars = 6000, CancellationToken cancellationToken = default, string? workingDirectory = null)
    {
        if (maxOutputChars < 500)
        {
            maxOutputChars = 500;
        }

        var result = new TraceResult();

        if (string.IsNullOrWhiteSpace(tracePath))
        {
            result.Status = "error";
            result.Errors.Add("Trace path is empty.");
            return SerializeResult(result, maxOutputChars);
        }

        try
        {
            var baseDirectory = string.IsNullOrWhiteSpace(workingDirectory)
                ? Environment.CurrentDirectory
                : Path.GetFullPath(workingDirectory);
            tracePath = Path.GetFullPath(tracePath, baseDirectory);
            result.TracePath = tracePath;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            result.Status = "error";
            result.Errors.Add($"Invalid trace path or working directory: {ex.Message}");
            return SerializeResult(result, maxOutputChars);
        }

        if (!File.Exists(tracePath))
        {
            result.Status = "error";
            result.Errors.Add($"Trace file not found: {tracePath}");
            return SerializeResult(result, maxOutputChars);
        }

        var state = new ParseState();

        try
        {
            await ProcessZipArchiveAsync(tracePath, result, state, maxOutputChars, cancellationToken);
            if (state.HasFatalError)
            {
                result.Status = "error";
            }
            else if (state.HasPartialOrTruncated)
            {
                result.Status = "partial";
            }
            else
            {
                result.Status = "inspected";
            }
        }
        catch (OperationCanceledException)
        {
            result.Status = "error";
            AddWarning(result, "Trace parsing was canceled.");
        }
        catch (InvalidDataException ex)
        {
            result.Status = "error";
            AddWarning(result, $"Invalid zip or archive format: {ex.Message}");
        }
        catch (Exception ex)
        {
            result.Status = "error";
            AddWarning(result, $"Unexpected error during trace parsing: {ex.Message}");
        }

        return SerializeResult(result, maxOutputChars);
    }

    private static async Task ProcessZipArchiveAsync(
        string tracePath,
        TraceResult result,
        ParseState state,
        int maxOutputChars,
        CancellationToken cancellationToken)
    {
        using var fileStream = new FileStream(tracePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var archive = new ZipArchive(fileStream, ZipArchiveMode.Read, leaveOpen: false);

        long totalArchiveUncompressedBytes = 0;
        foreach (var entry in archive.Entries)
        {
            totalArchiveUncompressedBytes += entry.Length;
        }

        if (totalArchiveUncompressedBytes > MaxTotalSizeBytes)
        {
            state.HasFatalError = true;
            AddWarning(result, $"Archive uncompressed total size ({totalArchiveUncompressedBytes} bytes) exceeds limit ({MaxTotalSizeBytes} bytes).");
            return;
        }

        var seenEntries = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var traceEntries = new List<ZipArchiveEntry>();
        var networkEntries = new List<ZipArchiveEntry>();
        var resourceEntries = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var fullName = entry.FullName.Replace('\\', '/');
            if (fullName.Contains("../") || fullName.StartsWith('/') || fullName.StartsWith("./"))
            {
                AddWarning(result, $"Suspicious entry path skipped: {fullName}");
                state.HasPartialOrTruncated = true;
                continue;
            }

            if (!seenEntries.Add(fullName))
            {
                AddWarning(result, $"Duplicate entry skipped: {fullName}");
                state.HasPartialOrTruncated = true;
                continue;
            }

            if (entry.Length > MaxEntrySizeBytes)
            {
                AddWarning(result, $"Entry exceeds maximum size limit ({entry.Length} bytes > {MaxEntrySizeBytes} bytes) and was skipped: {fullName}");
                state.HasPartialOrTruncated = true;
                continue;
            }

            if (fullName.EndsWith(".trace", StringComparison.OrdinalIgnoreCase))
            {
                traceEntries.Add(entry);
            }
            else if (fullName.EndsWith(".network", StringComparison.OrdinalIgnoreCase))
            {
                networkEntries.Add(entry);
            }
            else if (fullName.StartsWith("resources/", StringComparison.OrdinalIgnoreCase))
            {
                var resourceName = fullName.Substring("resources/".Length);
                if (!string.IsNullOrEmpty(resourceName))
                {
                    resourceEntries[resourceName] = entry;
                }
            }
        }

        var actions = new Dictionary<string, ActionCandidate>(StringComparer.Ordinal);
        var errors = new List<ActionError>();
        var domSnapshots = new List<FrameSnapshotData>();

        foreach (var entry in traceEntries)
        {
            if (state.IsTotalLimitReached) break;

            await ReadJsonLinesEntryAsync(entry, result, state, line =>
            {
                ParseTraceLine(line, actions, errors, result, domSnapshots);
            }, cancellationToken);
        }

        foreach (var entry in networkEntries)
        {
            if (state.IsTotalLimitReached) break;

            await ReadJsonLinesEntryAsync(entry, result, state, line =>
            {
                ParseNetworkLine(line, result, resourceEntries, state);
            }, cancellationToken);
        }

        var (failedCandidate, primaryError) = ResolveFailedAction(actions, errors, result);
        if (primaryError != null)
        {
            var failureMessage = StripAnsi(primaryError.ErrorMessage);
            result.UncaughtErrors.RemoveAll(error => string.Equals(error.Message, failureMessage, StringComparison.Ordinal));
        }
        foreach (var error in result.UncaughtErrors)
        {
            error.Message = ClipText(error.Message, 500) ?? "Unknown error";
        }
        ResolveLatestDomSnapshot(domSnapshots, result, failedCandidate, primaryError, maxOutputChars, state);
    }

    private static async Task ReadJsonLinesEntryAsync(
        ZipArchiveEntry entry,
        TraceResult result,
        ParseState state,
        Action<string> onLine,
        CancellationToken cancellationToken)
    {
        if (entry.Length > MaxEntrySizeBytes)
        {
            AddWarning(result, $"Entry exceeds maximum size ({entry.Length} bytes): {entry.FullName}");
            state.HasPartialOrTruncated = true;
            return;
        }

        using var stream = entry.Open();
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var safeReader = new SafeLineReader(reader, MaxLineLength);

        long entryRead = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (state.ProcessedLines >= MaxProcessedLines)
            {
                AddWarning(result, $"Maximum processed lines limit ({MaxProcessedLines}) reached in {entry.FullName}.");
                state.HasPartialOrTruncated = true;
                break;
            }

            var (line, lineChars, exceeded) = await safeReader.ReadLineAsync(cancellationToken);
            if (line == null && !exceeded)
            {
                break;
            }

            state.ProcessedLines++;
            long lineBytes = line != null ? Encoding.UTF8.GetByteCount(line) + 1 : lineChars + 1;
            entryRead += lineBytes;
            state.AddReadBytes(lineBytes, result, entry.FullName);

            if (entryRead > MaxEntrySizeBytes)
            {
                AddWarning(result, $"Entry read limit reached ({MaxEntrySizeBytes} bytes): {entry.FullName}");
                state.HasPartialOrTruncated = true;
                break;
            }

            if (state.IsTotalLimitReached)
            {
                break;
            }

            if (exceeded)
            {
                AddWarning(result, $"Line exceeds maximum length ({MaxLineLength} chars) in {entry.FullName} and was skipped.");
                state.HasPartialOrTruncated = true;
                continue;
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            try
            {
                onLine(line);
            }
            catch (JsonException)
            {
                AddWarning(result, $"Invalid JSON line encountered in {entry.FullName}");
                state.HasPartialOrTruncated = true;
            }
            catch (Exception ex)
            {
                AddWarning(result, $"Error parsing line in {entry.FullName}: {ex.Message}");
                state.HasPartialOrTruncated = true;
            }
        }
    }

    private static void ParseTraceLine(
        string line,
        Dictionary<string, ActionCandidate> actions,
        List<ActionError> errors,
        TraceResult result,
        List<FrameSnapshotData> domSnapshots)
    {
        using var doc = JsonDocument.Parse(line);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        if (!root.TryGetProperty("type", out var typeProp) || typeProp.ValueKind != JsonValueKind.String)
        {
            return;
        }

        var type = typeProp.GetString();
        switch (type)
        {
            case "before":
                {
                    var callId = root.TryGetProperty("callId", out var cProp) ? cProp.GetString() : null;
                    if (string.IsNullOrEmpty(callId)) return;

                    var method = root.TryGetProperty("method", out var mProp) ? mProp.GetString() : null;
                    var cls = root.TryGetProperty("class", out var clProp) ? clProp.GetString() : null;
                    var apiName = !string.IsNullOrEmpty(cls) && !string.IsNullOrEmpty(method) ? $"{cls}.{method}" : method ?? string.Empty;

                    string? selector = null;
                    if (root.TryGetProperty("params", out var paramsProp) && paramsProp.ValueKind == JsonValueKind.Object)
                    {
                        if (paramsProp.TryGetProperty("selector", out var selProp) && selProp.ValueKind == JsonValueKind.String)
                        {
                            selector = selProp.GetString();
                        }
                    }

                    double startTime = root.TryGetProperty("startTime", out var stProp) && stProp.TryGetDouble(out var st) ? st : 0;

                    if (actions.Count < 10000)
                    {
                        actions[callId] = new ActionCandidate
                        {
                            CallId = callId,
                            ApiName = apiName,
                            Selector = selector,
                            StartTime = startTime
                        };
                    }
                    break;
                }

            case "action":
                {
                    var callId = root.TryGetProperty("callId", out var cProp) ? cProp.GetString() : null;
                    if (string.IsNullOrEmpty(callId)) return;

                    var method = root.TryGetProperty("method", out var mProp) ? mProp.GetString() : (root.TryGetProperty("apiName", out var aProp) ? aProp.GetString() : null);
                    var cls = root.TryGetProperty("class", out var clProp) ? clProp.GetString() : null;
                    var apiName = !string.IsNullOrEmpty(cls) && !string.IsNullOrEmpty(method) ? $"{cls}.{method}" : method ?? string.Empty;

                    string? selector = null;
                    if (root.TryGetProperty("params", out var paramsProp) && paramsProp.ValueKind == JsonValueKind.Object)
                    {
                        if (paramsProp.TryGetProperty("selector", out var selProp) && selProp.ValueKind == JsonValueKind.String)
                        {
                            selector = selProp.GetString();
                        }
                    }

                    double startTime = root.TryGetProperty("startTime", out var stProp) && stProp.TryGetDouble(out var st) ? st : 0;

                    if (actions.Count < 10000)
                    {
                        actions[callId] = new ActionCandidate
                        {
                            CallId = callId,
                            ApiName = apiName,
                            Selector = selector,
                            StartTime = startTime
                        };
                    }

                    if (root.TryGetProperty("error", out var errProp))
                    {
                        var errMsg = ExtractErrorMessage(errProp);
                        if (!string.IsNullOrEmpty(errMsg) && errors.Count < 1000)
                        {
                            errors.Add(new ActionError
                            {
                                CallId = callId,
                                ErrorMessage = errMsg,
                                Time = root.TryGetProperty("endTime", out var etProp) && etProp.TryGetDouble(out var et) ? et : startTime
                            });
                        }
                    }
                    break;
                }

            case "after":
                {
                    var callId = root.TryGetProperty("callId", out var cProp) ? cProp.GetString() : null;
                    if (string.IsNullOrEmpty(callId)) return;

                    if (root.TryGetProperty("error", out var errProp))
                    {
                        var errMsg = ExtractErrorMessage(errProp);
                        if (!string.IsNullOrEmpty(errMsg) && errors.Count < 1000)
                        {
                            errors.Add(new ActionError
                            {
                                CallId = callId,
                                ErrorMessage = errMsg,
                                Time = root.TryGetProperty("endTime", out var etProp) && etProp.TryGetDouble(out var et) ? et : 0
                            });
                        }
                    }
                    break;
                }

            case "error":
                {
                    var msg = root.TryGetProperty("message", out var mProp) ? mProp.GetString() : null;
                    var stack = root.TryGetProperty("stack", out var sProp) ? ExtractStackFrames(sProp) : null;
                    if (string.IsNullOrEmpty(msg) && root.TryGetProperty("error", out var eProp))
                    {
                        msg = ExtractErrorMessage(eProp);
                    }

                    if (!string.IsNullOrEmpty(msg) || stack is { Count: > 0 })
                    {
                        if (result.UncaughtErrors.Count < MaxCollectionLimit)
                        {
                            result.UncaughtErrors.Add(new UncaughtErrorInfo
                            {
                                Message = StripAnsi(msg ?? "Unknown error"),
                                Stack = stack
                            });
                        }
                    }
                    break;
                }

            case "console":
                {
                    var text = root.TryGetProperty("text", out var tProp) ? tProp.GetString() : null;
                    var msgType = root.TryGetProperty("messageType", out var mtProp) ? mtProp.GetString() : "log";
                    double time = root.TryGetProperty("time", out var timeProp) && timeProp.TryGetDouble(out var tm) ? tm : 0;

                    if (!string.IsNullOrEmpty(text))
                    {
                        if (result.ConsoleErrors.Count < MaxCollectionLimit)
                        {
                            result.ConsoleErrors.Add(new ConsoleErrorInfo
                            {
                                Type = msgType ?? "log",
                                Text = ClipText(text, 500) ?? text,
                                Time = time
                            });
                        }
                    }
                    break;
                }

            case "frame-snapshot":
                {
                    if (domSnapshots.Count >= 500) return;

                    if (root.TryGetProperty("snapshot", out var snProp) && snProp.ValueKind == JsonValueKind.Object)
                    {
                        bool isMainFrame = snProp.TryGetProperty("isMainFrame", out var imfProp) && imfProp.GetBoolean();
                        if (isMainFrame && snProp.TryGetProperty("html", out var htmlProp))
                        {
                            var callId = snProp.TryGetProperty("callId", out var cProp) ? cProp.GetString() : null;
                            var frameId = snProp.TryGetProperty("frameId", out var fProp) ? fProp.GetString() : null;
                            double timestamp = snProp.TryGetProperty("timestamp", out var tsProp) && tsProp.TryGetDouble(out var ts) ? ts : 0;
                            var phase = snProp.TryGetProperty("phase", out var pProp) ? pProp.GetString() : null;

                            domSnapshots.Add(new FrameSnapshotData
                            {
                                CallId = callId,
                                FrameId = frameId,
                                IsMainFrame = isMainFrame,
                                Timestamp = timestamp,
                                Phase = phase,
                                HtmlElement = htmlProp.Clone()
                            });
                        }
                    }
                    break;
                }

            case "resource-snapshot":
                {
                    if (root.TryGetProperty("snapshot", out var resProp) && resProp.ValueKind == JsonValueKind.Object)
                    {
                        ExtractHttpError(resProp, result, null, null);
                    }
                    break;
                }
        }
    }

    private static void ParseNetworkLine(
        string line,
        TraceResult result,
        Dictionary<string, ZipArchiveEntry> resourceEntries,
        ParseState state)
    {
        using var doc = JsonDocument.Parse(line);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return;

        if (root.TryGetProperty("type", out var typeProp) && typeProp.GetString() == "resource-snapshot" &&
            root.TryGetProperty("snapshot", out var snProp) && snProp.ValueKind == JsonValueKind.Object)
        {
            ExtractHttpError(snProp, result, resourceEntries, state);
        }
        else if (root.TryGetProperty("request", out _) && root.TryGetProperty("response", out _))
        {
            ExtractHttpError(root, result, resourceEntries, state);
        }
    }

    private static void ExtractHttpError(
        JsonElement entryElement,
        TraceResult result,
        Dictionary<string, ZipArchiveEntry>? resourceEntries,
        ParseState? state)
    {
        if (result.HttpErrors.Count >= MaxCollectionLimit) return;

        int status = 0;
        string? method = null;
        string? url = null;
        string? requestBody = null;
        string? responseBody = null;

        if (entryElement.TryGetProperty("response", out var respProp) && respProp.ValueKind == JsonValueKind.Object)
        {
            if (respProp.TryGetProperty("status", out var stProp) && stProp.TryGetInt32(out var s))
            {
                status = s;
            }

            if (respProp.TryGetProperty("content", out var contentProp) && contentProp.ValueKind == JsonValueKind.Object)
            {
                if (contentProp.TryGetProperty("text", out var textProp) && textProp.ValueKind == JsonValueKind.String)
                {
                    responseBody = textProp.GetString();
                }
                else if (resourceEntries != null && contentProp.TryGetProperty("_sha1", out var shaProp) && shaProp.ValueKind == JsonValueKind.String)
                {
                    var sha = shaProp.GetString();
                    if (!string.IsNullOrEmpty(sha) && resourceEntries.TryGetValue(sha, out var resEntry))
                    {
                        responseBody = ReadEntrySnippet(resEntry, MaxResourceBodyChars, state, result);
                    }
                }
            }
        }

        if (status < 400) return;

        if (entryElement.TryGetProperty("request", out var reqProp) && reqProp.ValueKind == JsonValueKind.Object)
        {
            if (reqProp.TryGetProperty("method", out var mProp) && mProp.ValueKind == JsonValueKind.String)
            {
                method = mProp.GetString();
            }

            if (reqProp.TryGetProperty("url", out var uProp) && uProp.ValueKind == JsonValueKind.String)
            {
                url = SanitizeUrl(uProp.GetString());
            }

            if (reqProp.TryGetProperty("postData", out var postProp) && postProp.ValueKind == JsonValueKind.Object)
            {
                if (postProp.TryGetProperty("text", out var pTextProp) && pTextProp.ValueKind == JsonValueKind.String)
                {
                    requestBody = pTextProp.GetString();
                }
                else if (resourceEntries != null && postProp.TryGetProperty("_sha1", out var pShaProp) && pShaProp.ValueKind == JsonValueKind.String)
                {
                    var sha = pShaProp.GetString();
                    if (!string.IsNullOrEmpty(sha) && resourceEntries.TryGetValue(sha, out var resEntry))
                    {
                        requestBody = ReadEntrySnippet(resEntry, MaxResourceBodyChars, state, result);
                    }
                }
            }
        }

        result.HttpErrors.Add(new HttpErrorInfo
        {
            Method = method ?? "GET",
            Url = url ?? "unknown",
            Status = status,
            RequestBody = ClipText(requestBody, MaxResourceBodyChars),
            ResponseBody = ClipText(responseBody, MaxResourceBodyChars)
        });
    }

    private static string? ReadEntrySnippet(
        ZipArchiveEntry entry,
        int maxChars,
        ParseState? state,
        TraceResult result)
    {
        try
        {
            if (entry.Length > MaxEntrySizeBytes) return null;

            using var stream = entry.Open();
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var buffer = new char[maxChars];
            int read = reader.ReadBlock(buffer, 0, maxChars);
            if (read > 0)
            {
                state?.AddReadBytes(read * 2, result, entry.FullName);
                return new string(buffer, 0, read);
            }
            return null;
        }
        catch
        {
            return null;
        }
    }

    private static (ActionCandidate? Candidate, ActionError? PrimaryError) ResolveFailedAction(
        Dictionary<string, ActionCandidate> actions,
        List<ActionError> errors,
        TraceResult result)
    {
        if (errors.Count == 0) return (null, null);

        var primaryError = errors[0];
        actions.TryGetValue(primaryError.CallId, out var candidate);

        result.FailedAction = new FailedActionInfo
        {
            CallId = primaryError.CallId,
            ApiName = candidate?.ApiName ?? "unknown",
            Selector = candidate?.Selector,
            Error = ClipText(primaryError.ErrorMessage, 1000) ?? "Unknown action error"
        };

        return (candidate, primaryError);
    }

    private static void ResolveLatestDomSnapshot(
        List<FrameSnapshotData> domSnapshots,
        TraceResult result,
        ActionCandidate? failedCandidate,
        ActionError? primaryError,
        int maxOutputChars,
        ParseState state)
    {
        if (domSnapshots.Count == 0) return;

        FrameSnapshotData? targetSnapshot = null;
        int targetIndex = -1;

        if (result.FailedAction != null && primaryError != null)
        {
            double failureTime = primaryError.Time > 0 ? primaryError.Time : (failedCandidate?.StartTime ?? double.MaxValue);

            // 1. 同一 callId かつ failureTime 以前 (または phase == "before")
            for (int i = domSnapshots.Count - 1; i >= 0; i--)
            {
                var s = domSnapshots[i];
                if (s.CallId == result.FailedAction.CallId)
                {
                    if (string.Equals(s.Phase, "before", StringComparison.OrdinalIgnoreCase) || s.Timestamp <= failureTime)
                    {
                        targetSnapshot = s;
                        targetIndex = i;
                        break;
                    }
                }
            }

            // 2. なければ、mainFrame かつ failureTime 以前の最新
            if (targetSnapshot == null)
            {
                for (int i = domSnapshots.Count - 1; i >= 0; i--)
                {
                    var s = domSnapshots[i];
                    if (s.IsMainFrame && s.Timestamp <= failureTime)
                    {
                        targetSnapshot = s;
                        targetIndex = i;
                        break;
                    }
                }
            }
        }

        // 3. 失敗がない場合、または見つからなかった場合は最新の snapshot
        if (targetSnapshot == null)
        {
            targetIndex = domSnapshots.Count - 1;
            targetSnapshot = domSnapshots[targetIndex];
        }

        var sb = new StringBuilder();
        int maxSnapshotChars = Math.Min(2500, maxOutputChars / 2);

        try
        {
            RenderDomNode(targetSnapshot.HtmlElement, targetIndex, domSnapshots, result, sb, maxSnapshotChars, depth: 0, state);
            result.LatestDomSnapshot = sb.ToString();
        }
        catch (Exception ex)
        {
            AddWarning(result, $"DOM snapshot render error: {ex.Message}");
            state.HasPartialOrTruncated = true;
            result.LatestDomSnapshot = null;
        }
    }

    private static void RenderDomNode(
        JsonElement node,
        int currentSnapshotIndex,
        List<FrameSnapshotData> allSnapshots,
        TraceResult result,
        StringBuilder sb,
        int maxChars,
        int depth,
        ParseState state)
    {
        if (sb.Length >= maxChars) return;

        if (depth > 100)
        {
            AddWarning(result, "DOM subtree recursion depth exceeded limit (> 100).");
            state.HasPartialOrTruncated = true;
            sb.Append("<!-- recursion depth exceeded -->");
            return;
        }

        if (node.ValueKind == JsonValueKind.String)
        {
            var text = node.GetString();
            if (!string.IsNullOrEmpty(text))
            {
                sb.Append(HtmlEncode(text));
            }
            return;
        }

        if (node.ValueKind != JsonValueKind.Array) return;

        int arrayLen = node.GetArrayLength();
        if (arrayLen == 0) return;

        var first = node[0];

        // 参照ノード [ [ delta, nodeIndex ] ]
        if (first.ValueKind == JsonValueKind.Array)
        {
            if (first.GetArrayLength() >= 2 &&
                first[0].TryGetInt32(out int delta) &&
                first[1].TryGetInt32(out int nodeIndex))
            {
                int refIndex = currentSnapshotIndex - delta;
                if (delta > 0 && refIndex >= 0 && refIndex < allSnapshots.Count)
                {
                    var refSnapshot = allSnapshots[refIndex];
                    var refNodes = refSnapshot.GetNodes();
                    if (nodeIndex >= 0 && nodeIndex < refNodes.Count)
                    {
                        RenderDomNode(refNodes[nodeIndex], refIndex, allSnapshots, result, sb, maxChars, depth + 1, state);
                        return;
                    }
                }

                AddWarning(result, $"Unresolved DOM subtree reference (delta={delta}, index={nodeIndex})");
                state.HasPartialOrTruncated = true;
                sb.Append("<!-- unresolved subtree reference -->");
            }
            return;
        }

        if (first.ValueKind == JsonValueKind.String)
        {
            var tagName = first.GetString();
            if (string.IsNullOrEmpty(tagName) || ExcludedTags.Contains(tagName))
            {
                return;
            }

            sb.Append('<').Append(tagName);

            if (arrayLen > 1 && node[1].ValueKind == JsonValueKind.Object)
            {
                foreach (var attr in node[1].EnumerateObject())
                {
                    var attrName = attr.Name;
                    if (attrName.StartsWith("on", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var attrVal = attr.Value.ValueKind == JsonValueKind.String ? attr.Value.GetString() : attr.Value.ToString();
                    sb.Append(' ').Append(attrName).Append("=\"").Append(HtmlEncode(attrVal ?? string.Empty)).Append('"');
                }
            }

            if (VoidElements.Contains(tagName))
            {
                sb.Append(" />");
                return;
            }

            sb.Append('>');

            for (int i = 2; i < arrayLen; i++)
            {
                if (sb.Length >= maxChars) break;
                RenderDomNode(node[i], currentSnapshotIndex, allSnapshots, result, sb, maxChars, depth + 1, state);
            }

            sb.Append("</").Append(tagName).Append('>');
        }
    }

    private static string SanitizeUrl(string? rawUrl)
    {
        if (string.IsNullOrEmpty(rawUrl))
        {
            return "unknown";
        }

        var sanitized = CredentialsRegex.Replace(rawUrl, "$1:***@");
        sanitized = SensitiveParamRegex.Replace(sanitized, "$1=[REDACTED]");
        return sanitized;
    }

    private static string? ExtractErrorMessage(JsonElement errorProp)
    {
        if (errorProp.ValueKind == JsonValueKind.String)
        {
            return errorProp.GetString();
        }

        if (errorProp.ValueKind == JsonValueKind.Object)
        {
            if (errorProp.TryGetProperty("message", out var mProp) && mProp.ValueKind == JsonValueKind.String)
            {
                return mProp.GetString();
            }

            if (errorProp.TryGetProperty("error", out var innerProp))
            {
                return ExtractErrorMessage(innerProp);
            }
        }

        return null;
    }

    private static string StripAnsi(string text) => AnsiRegex.Replace(text, string.Empty);

    private static List<string>? ExtractStackFrames(JsonElement stack)
    {
        var frames = new List<string>();
        if (stack.ValueKind == JsonValueKind.String)
        {
            foreach (var line in (stack.GetString() ?? string.Empty).Split('\n'))
            {
                var frame = StripAnsi(line).Trim();
                if (frame.StartsWith("at ", StringComparison.Ordinal)) frames.Add(frame);
            }
        }
        else if (stack.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in stack.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String)
                {
                    frames.Add(StripAnsi(item.GetString() ?? string.Empty).Trim());
                }
                else if (item.ValueKind == JsonValueKind.Object && item.TryGetProperty("file", out var file) && file.ValueKind == JsonValueKind.String)
                {
                    var location = file.GetString() ?? string.Empty;
                    if (item.TryGetProperty("line", out var line)) location += $":{line}";
                    if (item.TryGetProperty("column", out var column)) location += $":{column}";
                    var function = item.TryGetProperty("function", out var fn) && fn.ValueKind == JsonValueKind.String ? fn.GetString() : null;
                    frames.Add(StripAnsi(string.IsNullOrEmpty(function) ? $"at {location}" : $"at {function} ({location})").Trim());
                }
            }
        }

        return frames.Where(frame => frame.Length > 0 && !frame.Contains("node:internal", StringComparison.OrdinalIgnoreCase)
            && !PlaywrightFrameRegex.IsMatch(frame.Replace('\\', '/')))
            .Take(MaxCollectionLimit).Select(frame => ClipText(frame, 1000) ?? string.Empty).ToList();
    }

    private static string? ClipText(string? text, int maxChars)
    {
        if (text == null)
        {
            return null;
        }

        text = StripAnsi(text);
        if (text.Length <= maxChars)
        {
            return text;
        }

        return text.Substring(0, maxChars) + "...";
    }

    private static string HtmlEncode(string text)
    {
        return text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
    }

    private static void AddWarning(TraceResult result, string warning)
    {
        if (result.Warnings.Count < MaxCollectionLimit)
        {
            result.Warnings.Add(warning);
        }
        else if (result.Warnings.Count == MaxCollectionLimit)
        {
            result.Warnings.Add("Maximum warning limit reached. Subsequent warnings suppressed.");
        }
    }

    private static string SerializeResult(TraceResult result, int maxOutputChars)
    {
        var options = new JsonSerializerOptions
        {
            WriteIndented = false,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };
        options.Converters.Add(new AnsiFreeStringConverter());

        var json = JsonSerializer.Serialize(result, options);
        if (json.Length <= maxOutputChars)
        {
            return json;
        }

        // 1. DOM スナップショットを段階的に縮小
        while (!string.IsNullOrEmpty(result.LatestDomSnapshot) && json.Length > maxOutputChars)
        {
            if (result.LatestDomSnapshot.Length > 200)
            {
                result.LatestDomSnapshot = result.LatestDomSnapshot.Substring(0, result.LatestDomSnapshot.Length / 2) + "... [truncated]";
            }
            else
            {
                result.LatestDomSnapshot = null;
            }
            json = JsonSerializer.Serialize(result, options);
        }

        if (json.Length <= maxOutputChars) return json;

        // 2. 配列の削減
        while (json.Length > maxOutputChars)
        {
            bool changed = false;

            if (result.ConsoleErrors.Count > 1)
            {
                result.ConsoleErrors = result.ConsoleErrors.Take(result.ConsoleErrors.Count / 2).ToList();
                changed = true;
            }
            else if (result.HttpErrors.Count > 1)
            {
                result.HttpErrors = result.HttpErrors.Take(result.HttpErrors.Count / 2).ToList();
                changed = true;
            }
            else if (result.Warnings.Count > 1)
            {
                result.Warnings = result.Warnings.Take(result.Warnings.Count / 2).ToList();
                changed = true;
            }
            else if (result.UncaughtErrors.Count > 1)
            {
                result.UncaughtErrors = result.UncaughtErrors.Take(1).ToList();
                changed = true;
            }
            else
            {
                break;
            }

            if (changed)
            {
                json = JsonSerializer.Serialize(result, options);
            }
        }

        if (json.Length <= maxOutputChars) return json;

        // 3. フィールドクリッピング
        foreach (var http in result.HttpErrors)
        {
            http.RequestBody = null;
            http.ResponseBody = null;
        }
        foreach (var uncaught in result.UncaughtErrors)
        {
            uncaught.Stack = null;
            uncaught.Message = ClipText(uncaught.Message, 100) ?? string.Empty;
        }
        foreach (var console in result.ConsoleErrors)
        {
            console.Text = ClipText(console.Text, 100) ?? string.Empty;
        }

        json = JsonSerializer.Serialize(result, options);
        if (json.Length <= maxOutputChars) return json;

        // 4. 配列クリア (FailedActionは最優先保持)
        result.ConsoleErrors.Clear();
        result.HttpErrors.Clear();
        result.UncaughtErrors.Clear();
        result.Warnings = new List<string> { "Output truncated to fit maxOutputChars limit." };

        json = JsonSerializer.Serialize(result, options);
        if (json.Length <= maxOutputChars) return json;

        // 5. FailedAction.Error も clip (selector / apiName は優先保持)
        if (result.FailedAction != null)
        {
            int availableForError = Math.Max(50, maxOutputChars - 250);
            result.FailedAction.Error = ClipText(result.FailedAction.Error, availableForError) ?? "Action failed";
        }

        json = JsonSerializer.Serialize(result, options);
        if (json.Length <= maxOutputChars) return json;

        // 6. 究極 fallback: 最小の有効な JSON
        var emergency = new
        {
            status = result.Status,
            failedAction = result.FailedAction != null ? new
            {
                apiName = result.FailedAction.ApiName,
                selector = result.FailedAction.Selector,
                error = ClipText(result.FailedAction.Error, Math.Max(30, maxOutputChars - 150))
            } : null,
            truncated = true
        };
        json = JsonSerializer.Serialize(emergency, options);

        if (json.Length <= maxOutputChars) return json;

        return JsonSerializer.Serialize(new { status = result.Status, truncated = true }, options);
    }

    private sealed class SafeLineReader
    {
        private readonly StreamReader _reader;
        private readonly char[] _buffer = new char[8192];
        private int _bufferPos = 0;
        private int _bufferLength = 0;
        private readonly StringBuilder _sb = new();
        private readonly int _maxChars;

        public SafeLineReader(StreamReader reader, int maxChars)
        {
            _reader = reader;
            _maxChars = maxChars;
        }

        public async Task<(string? Line, long ReadChars, bool Exceeded)> ReadLineAsync(CancellationToken ct)
        {
            _sb.Clear();
            bool exceeded = false;
            long readChars = 0;

            while (true)
            {
                if (_bufferPos >= _bufferLength)
                {
                    _bufferPos = 0;
                    _bufferLength = await _reader.ReadAsync(_buffer, 0, _buffer.Length);
                    if (_bufferLength == 0)
                    {
                        if (_sb.Length == 0 && !exceeded)
                        {
                            return (null, 0, false);
                        }
                        var resStr = exceeded ? null : _sb.ToString();
                        return (resStr, readChars, exceeded);
                    }
                }

                char c = _buffer[_bufferPos++];
                readChars++;

                if (c == '\r')
                {
                    continue;
                }

                if (c == '\n')
                {
                    var resStr = exceeded ? null : _sb.ToString();
                    return (resStr, readChars, exceeded);
                }

                if (!exceeded)
                {
                    if (_sb.Length < _maxChars)
                    {
                        _sb.Append(c);
                    }
                    else
                    {
                        exceeded = true;
                        _sb.Clear();
                    }
                }
            }
        }
    }

    private sealed class ParseState
    {
        public long TotalBytesRead { get; private set; }
        public bool HasFatalError { get; set; }
        public bool HasPartialOrTruncated { get; set; }
        public int ProcessedLines { get; set; }

        public bool IsTotalLimitReached => TotalBytesRead > MaxTotalSizeBytes;

        public void AddReadBytes(long bytes, TraceResult result, string entryName)
        {
            TotalBytesRead += bytes;
            if (TotalBytesRead > MaxTotalSizeBytes)
            {
                HasPartialOrTruncated = true;
                AddWarning(result, $"Total uncompressed read limit reached ({MaxTotalSizeBytes} bytes) while reading {entryName}.");
            }
        }
    }

    private sealed class TraceResult
    {
        public string Status { get; set; } = "inspected";
        public string? TracePath { get; set; }
        public List<string> Errors { get; set; } = new();
        public FailedActionInfo? FailedAction { get; set; }
        public List<HttpErrorInfo> HttpErrors { get; set; } = new();
        public List<ConsoleErrorInfo> ConsoleErrors { get; set; } = new();
        public List<UncaughtErrorInfo> UncaughtErrors { get; set; } = new();
        public string? LatestDomSnapshot { get; set; }
        public List<string> Warnings { get; set; } = new();
    }

    private sealed class FailedActionInfo
    {
        public string CallId { get; set; } = string.Empty;
        public string ApiName { get; set; } = string.Empty;
        public string? Selector { get; set; }
        public string Error { get; set; } = string.Empty;
    }

    private sealed class HttpErrorInfo
    {
        public string Method { get; set; } = string.Empty;
        public string Url { get; set; } = string.Empty;
        public int Status { get; set; }
        public string? RequestBody { get; set; }
        public string? ResponseBody { get; set; }
    }

    private sealed class ConsoleErrorInfo
    {
        public string Type { get; set; } = string.Empty;
        public string Text { get; set; } = string.Empty;
        public double Time { get; set; }
    }

    private sealed class UncaughtErrorInfo
    {
        public string Message { get; set; } = string.Empty;
        public List<string>? Stack { get; set; }
    }

    private sealed class AnsiFreeStringConverter : JsonConverter<string>
    {
        public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => reader.GetString();

        public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options) => writer.WriteStringValue(StripAnsi(value));
    }

    private sealed class ActionCandidate
    {
        public string CallId { get; set; } = string.Empty;
        public string ApiName { get; set; } = string.Empty;
        public string? Selector { get; set; }
        public double StartTime { get; set; }
    }

    private sealed class ActionError
    {
        public string CallId { get; set; } = string.Empty;
        public string ErrorMessage { get; set; } = string.Empty;
        public double Time { get; set; }
    }

    private sealed class FrameSnapshotData
    {
        public string? CallId { get; set; }
        public string? FrameId { get; set; }
        public bool IsMainFrame { get; set; }
        public double Timestamp { get; set; }
        public string? Phase { get; set; }
        public JsonElement HtmlElement { get; set; }

        private List<JsonElement>? _nodes;

        public List<JsonElement> GetNodes()
        {
            if (_nodes == null)
            {
                _nodes = new List<JsonElement>();
                CollectNodes(HtmlElement, _nodes, 0);
            }
            return _nodes;
        }

        private static void CollectNodes(JsonElement node, List<JsonElement> nodes, int depth)
        {
            if (depth > 100) return;

            if (node.ValueKind == JsonValueKind.String)
            {
                nodes.Add(node);
                return;
            }

            if (node.ValueKind == JsonValueKind.Array && node.GetArrayLength() > 0)
            {
                var first = node[0];
                if (first.ValueKind == JsonValueKind.String)
                {
                    int len = node.GetArrayLength();
                    for (int i = 2; i < len; i++)
                    {
                        CollectNodes(node[i], nodes, depth + 1);
                    }
                    nodes.Add(node);
                }
            }
        }
    }
}
