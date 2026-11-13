using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace InazumaPwsh;

public sealed class PowerShellSession : IDisposable
{
    private readonly SemaphoreSlim _semaphore = new(1, 1);
    private Process? _process;
    private WindowsProcessJob? _job;
    private NamedPipeServerStream? _pipeServer;
    private StreamReader? _reader;
    private StreamWriter? _writer;
    private bool _disposed;
    private volatile bool _stopping;
    private long _nextRequestId;

    public bool IsStopping => _stopping;

    public void Initialize()
    {
        _semaphore.Wait();
        try
        {
            EnsureWorkerRunningUnderLock().GetAwaiter().GetResult();
        }
        finally
        {
            _semaphore.Release();
        }
    }

    private static string ResolvePwshPath()
    {
        var overridePath = Environment.GetEnvironmentVariable("INAZUMA_PWSH_PATH");
        if (!string.IsNullOrWhiteSpace(overridePath))
        {
            if (File.Exists(overridePath))
            {
                return Path.GetFullPath(overridePath);
            }
            throw new FileNotFoundException($"The specified INAZUMA_PWSH_PATH does not exist: {overridePath}");
        }

        var pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrEmpty(pathEnv))
        {
            var dirs = pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            foreach (var dir in dirs)
            {
                var candidate = Path.Combine(dir, "pwsh.exe");
                if (File.Exists(candidate)) return Path.GetFullPath(candidate);
                var candidateNoExt = Path.Combine(dir, "pwsh");
                if (File.Exists(candidateNoExt)) return Path.GetFullPath(candidateNoExt);
            }
        }

        throw new FileNotFoundException("Could not locate pwsh.exe in PATH. PowerShell 7 or higher is required.");
    }

    private static string ResolveWorkerPath()
    {
        var processDir = Path.GetDirectoryName(Environment.ProcessPath);
        if (!string.IsNullOrEmpty(processDir))
        {
            var candidate = Path.Combine(processDir, "powershell-worker.ps1");
            if (File.Exists(candidate)) return Path.GetFullPath(candidate);
        }

        var baseDir = AppContext.BaseDirectory;
        var candidateBase = Path.Combine(baseDir, "powershell-worker.ps1");
        if (File.Exists(candidateBase)) return Path.GetFullPath(candidateBase);

        throw new FileNotFoundException($"Could not locate powershell-worker.ps1. ProcessPath={Environment.ProcessPath}, BaseDir={baseDir}");
    }

    private static async Task DrainAsync(StreamReader reader, StringBuilder? errorBuffer = null)
    {
        var buffer = new char[1024];
        try
        {
            int count;
            while ((count = await reader.ReadAsync(buffer.AsMemory())) > 0)
            {
                if (errorBuffer == null) continue;
                lock (errorBuffer)
                {
                    var remaining = 4000 - errorBuffer.Length;
                    if (remaining > 0) errorBuffer.Append(buffer, 0, Math.Min(remaining, count));
                }
            }
        }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
    }

    private async Task EnsureWorkerRunningUnderLock()
    {
        if (_process != null && !_process.HasExited && _pipeServer != null && _pipeServer.IsConnected)
        {
            return;
        }

        KillWorkerUnderLock();

        var pwshPath = ResolvePwshPath();
        var workerPath = ResolveWorkerPath();
        var pipeName = $"inazuma-pwsh-{Guid.NewGuid():N}";

        var pipeServer = new NamedPipeServerStream(
            pipeName,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

        var startInfo = new ProcessStartInfo(pwshPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        startInfo.ArgumentList.Add("-NoLogo");
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-File");
        startInfo.ArgumentList.Add(workerPath);
        startInfo.ArgumentList.Add(pipeName);

        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };

        var stderrBuffer = new StringBuilder();

        if (!process.Start())
        {
            pipeServer.Dispose();
            throw new InvalidOperationException($"Failed to start external PowerShell process: {pwshPath}");
        }

        WindowsProcessJob? job = null;
        try
        {
            job = WindowsProcessJob.Attach(process);
        }
        catch (Exception ex)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            pipeServer.Dispose();
            throw new InvalidOperationException($"Failed to attach PowerShell process to Windows Job Object: {ex.Message}", ex);
        }

        _ = DrainAsync(process.StandardError, stderrBuffer);
        _ = DrainAsync(process.StandardOutput);

        using var connectCts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try
        {
            await pipeServer.WaitForConnectionAsync(connectCts.Token);
        }
        catch (Exception ex)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            job.Dispose();
            pipeServer.Dispose();
            string err;
            lock (stderrBuffer) { err = stderrBuffer.ToString(); }
            throw new InvalidOperationException($"PowerShell worker failed to connect within timeout: {ex.Message}. Process stderr: {err}", ex);
        }

        var reader = new StreamReader(pipeServer, new UTF8Encoding(false), leaveOpen: true);
        var writer = new StreamWriter(pipeServer, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true, NewLine = "\n" };

        string? handshakeLine = null;
        using var handshakeCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            handshakeLine = await reader.ReadLineAsync().WaitAsync(handshakeCts.Token);
        }
        catch (Exception ex)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            job.Dispose();
            reader.Dispose();
            writer.Dispose();
            pipeServer.Dispose();
            throw new InvalidOperationException($"Failed to read handshake from worker: {ex.Message}", ex);
        }

        if (string.IsNullOrEmpty(handshakeLine))
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            job.Dispose();
            reader.Dispose();
            writer.Dispose();
            pipeServer.Dispose();
            string err;
            lock (stderrBuffer) { err = stderrBuffer.ToString(); }
            throw new InvalidOperationException($"PowerShell worker closed pipe before handshake. Process stderr: {err}");
        }

        using var doc = JsonDocument.Parse(handshakeLine);
        var root = doc.RootElement;
        var versionStr = root.GetProperty("version").GetString() ?? "";
        if (!Version.TryParse(versionStr.Split('-')[0], out var psVersion) || psVersion.Major < 7)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            job.Dispose();
            reader.Dispose();
            writer.Dispose();
            pipeServer.Dispose();
            throw new InvalidOperationException($"PowerShell 7 or higher is required. Detected version: {versionStr}");
        }

        _process = process;
        _job = job;
        _pipeServer = pipeServer;
        _reader = reader;
        _writer = writer;
    }

    private void KillWorkerUnderLock()
    {
        try { _writer?.Dispose(); } catch { }
        try { _reader?.Dispose(); } catch { }
        try { _pipeServer?.Dispose(); } catch { }
        try
        {
            if (_process != null && !_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
            }
        }
        catch { }
        try { _job?.Dispose(); } catch { }
        try { _process?.Dispose(); } catch { }

        _writer = null;
        _reader = null;
        _pipeServer = null;
        _process = null;
        _job = null;
    }

    public async Task<string> ResetSessionAsync(CancellationToken cancellationToken = default)
    {
        if (!await EnterSessionAsync(cancellationToken)) return StoppingResponse();
        try
        {
            await EnsureWorkerRunningUnderLock();

            var reqId = Interlocked.Increment(ref _nextRequestId);
            var reqJson = JsonSerializer.Serialize(new { action = "reset", id = reqId });
            await _writer!.WriteLineAsync(reqJson);

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cts.Token, cancellationToken);
            var responseLine = await _reader!.ReadLineAsync().WaitAsync(linked.Token);

            if (responseLine == null)
            {
                KillWorkerUnderLock();
                await EnsureWorkerRunningUnderLock();
            }

            return JsonSerializer.Serialize(new
            {
                status = "success",
                data = "PowerShell session has been reset successfully.",
                errors = Array.Empty<string>(),
                warnings = Array.Empty<string>(),
                durationMs = 0L
            });
        }
        finally
        {
            _semaphore.Release();
        }
    }

    public async Task<string> ExecuteScriptAsync(string script, int timeoutSeconds = 60, CancellationToken cancellationToken = default,
        string? workingDirectory = null, int maxOutputChars = 6000, bool compactResponse = false)
    {
        if (workingDirectory != null && (!Path.IsPathFullyQualified(workingDirectory) || !Directory.Exists(workingDirectory)))
        {
            return JsonSerializer.Serialize(new { status = "error", data = (object?)null,
                errors = new[] { "workingDirectory must be an existing absolute directory." },
                warnings = Array.Empty<string>(), durationMs = 0L });
        }

        if (!await EnterSessionAsync(cancellationToken)) return StoppingResponse();

        bool deferredCleanup = false;
        var sw = Stopwatch.StartNew();
        try
        {
            await EnsureWorkerRunningUnderLock();

            var reqId = Interlocked.Increment(ref _nextRequestId);
            var reqObj = new
            {
                action = "execute",
                id = reqId,
                script,
                workingDirectory
            };
            var reqJson = JsonSerializer.Serialize(reqObj);
            await _writer!.WriteLineAsync(reqJson);

            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Max(1, timeoutSeconds)));
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token, cancellationToken);

            var readTask = _reader!.ReadLineAsync();

            string? responseLine;
            bool isTimeout = false;
            bool isCancelled = false;
            try
            {
                responseLine = await readTask.WaitAsync(linkedCts.Token);
            }
            catch (OperationCanceledException)
            {
                isTimeout = timeoutCts.IsCancellationRequested;
                isCancelled = !isTimeout;
                _stopping = true;
                deferredCleanup = true;

                var stopJson = JsonSerializer.Serialize(new { action = "stop", id = reqId });
                try { await _writer.WriteLineAsync(stopJson); } catch { }

                var cleanup = Task.Run(async () =>
                {
                    try
                    {
                        using var grace = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                        await readTask.WaitAsync(grace.Token);
                    }
                    catch
                    {
                        KillWorkerUnderLock();
                    }
                    finally
                    {
                        _semaphore.Release();
                        _stopping = false;
                    }
                });

                await Task.WhenAny(cleanup, Task.Delay(150));
                sw.Stop();
                return JsonSerializer.Serialize(new
                {
                    status = isTimeout ? "timeout" : "cancelled",
                    data = (object?)null,
                    errors = new[] { isTimeout ? $"Execution timed out after {timeoutSeconds} seconds." : "Execution cancelled by the client." },
                    warnings = new[] {
                        "Stop requested; blocking operations may continue. This session cannot be reused until they finish.",
                        "If the worker cannot stop within 10 seconds, it will be terminated and all session state will be lost."
                    },
                    durationMs = sw.ElapsedMilliseconds
                });
            }
            catch (Exception ex)
            {
                sw.Stop();
                KillWorkerUnderLock();
                return JsonSerializer.Serialize(new
                {
                    status = "error",
                    data = (object?)null,
                    errors = new[] { ex.Message },
                    warnings = new[] { "The PowerShell worker was terminated. Session state was lost; the next execution starts a new worker." },
                    durationMs = sw.ElapsedMilliseconds
                });
            }

            sw.Stop();

            if (responseLine == null)
            {
                KillWorkerUnderLock();
                return JsonSerializer.Serialize(new
                {
                    status = "error",
                    data = (object?)null,
                    errors = new[] { "PowerShell worker process terminated unexpectedly. Session state was lost; the next execution starts a new worker." },
                    warnings = Array.Empty<string>(),
                    durationMs = sw.ElapsedMilliseconds
                });
            }

            using var doc = JsonDocument.Parse(responseLine);
            var root = doc.RootElement;

            var stopped = root.TryGetProperty("stopped", out var stopProp) && stopProp.GetBoolean();
            if (stopped)
            {
                return JsonSerializer.Serialize(new
                {
                    status = "cancelled",
                    data = (object?)null,
                    errors = new[] { "Execution cancelled by the client." },
                    warnings = Array.Empty<string>(),
                    durationMs = sw.ElapsedMilliseconds
                });
            }

            var errors = root.GetProperty("errors").EnumerateArray().Select(x => x.GetString() ?? "").ToList();
            var warnings = root.GetProperty("warnings").EnumerateArray().Select(x => x.GetString() ?? "").ToArray();
            var exitCode = root.GetProperty("exitCode").GetInt32();

            if (exitCode != 0 && !errors.Any(e => e.Contains($"exited with code {exitCode}")))
            {
                errors.Add($"Native command exited with code {exitCode}.");
            }

            var status = errors.Count > 0 ? "error" : "success";

            object? dataObject = null;
            if (root.GetProperty("dataIsString").GetBoolean())
            {
                dataObject = root.GetProperty("dataString").GetString();
            }
            else if (root.TryGetProperty("dataJson", out var dataJsonProp) && dataJsonProp.ValueKind == JsonValueKind.String)
            {
                var rawJson = dataJsonProp.GetString();
                if (!string.IsNullOrEmpty(rawJson))
                {
                    using var parsedDoc = JsonDocument.Parse(rawJson);
                    dataObject = parsedDoc.RootElement.Clone();
                }
            }

            var allStrings = root.GetProperty("allStrings").GetBoolean();
            var outputStrings = root.GetProperty("outputStrings").EnumerateArray().Select(x => x.GetString() ?? "").ToList();
            var infoOutputs = root.GetProperty("infoOutputs").EnumerateArray().Select(x => x.GetString() ?? "").ToList();

            string text;
            if (allStrings)
            {
                text = string.Join(Environment.NewLine, outputStrings.Concat(infoOutputs));
            }
            else if (dataObject is string str)
            {
                text = str;
            }
            else
            {
                text = JsonSerializer.Serialize(dataObject);
            }

            var compressed = await OutputCompressor.CompressAsync(text, maxOutputChars);

            return JsonSerializer.Serialize(new
            {
                status,
                data = compressed.IsTruncated ? null : dataObject,
                errors = errors.ToArray(),
                warnings,
                durationMs = sw.ElapsedMilliseconds,
                exitCode,
                isTruncated = compressed.IsTruncated,
                fullLogPath = compressed.FullLogPath,
                output = compactResponse && !compressed.IsTruncated && dataObject != null &&
                    compressed.Content == (dataObject is string dataText ? dataText : JsonSerializer.Serialize(dataObject))
                    ? null : compressed.Content,
                stats = new { compressed.OriginalChars, compressed.OriginalLines }
            });
        }
        finally
        {
            if (!deferredCleanup)
            {
                _semaphore.Release();
            }
        }
    }

    private static string StoppingResponse() => JsonSerializer.Serialize(new
    {
        status = "error",
        data = (object?)null,
        errors = new[] { "The previous command is still stopping; retry after it finishes." },
        warnings = Array.Empty<string>(),
        durationMs = 0L
    });

    private async Task<bool> EnterSessionAsync(CancellationToken cancellationToken)
    {
        if (_stopping) return await _semaphore.WaitAsync(TimeSpan.FromMilliseconds(250), cancellationToken);
        await _semaphore.WaitAsync(cancellationToken);
        return true;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _semaphore.Wait();
        try
        {
            KillWorkerUnderLock();
        }
        finally
        {
            _semaphore.Release();
            _semaphore.Dispose();
        }
    }
}
