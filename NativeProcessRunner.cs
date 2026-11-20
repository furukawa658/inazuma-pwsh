using System.Diagnostics;
using System.Text;

namespace InazumaPwsh;

public static class NativeProcessRunner
{
    public sealed record Result(string Status, int ExitCode, string Tail, string FullLogPath, long DurationMs, string? StdoutLogPath = null);

    public static async Task<Result> RunAsync(string executable, IEnumerable<string> arguments, string workingDirectory,
        int timeoutSeconds, IReadOnlyDictionary<string, string>? environment, CancellationToken cancellationToken, bool separateStdoutLog = false)
    {
        if (!Path.IsPathFullyQualified(workingDirectory) || !Directory.Exists(workingDirectory))
            throw new ArgumentException("workingDirectory must be an existing absolute directory.");
        var directory = Path.Combine(Path.GetTempPath(), "inazuma-pwsh-logs");
        Directory.CreateDirectory(directory);
        var id = Guid.NewGuid().ToString("N");
        var logPath = Path.Combine(directory, $"{id}.log");
        var stdoutLogPath = separateStdoutLog ? Path.Combine(directory, $"{id}.stdout.log") : null;
        var start = new ProcessStartInfo(executable)
        {
            WorkingDirectory = workingDirectory, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        if (environment != null)
            foreach (var pair in environment) start.Environment[pair.Key] = pair.Value;

        using var process = new Process { StartInfo = start };
        using var writer = new StreamWriter(logPath, false, new UTF8Encoding(false));
        using StreamWriter? stdoutWriter = stdoutLogPath == null ? null : new StreamWriter(stdoutLogPath, false, new UTF8Encoding(false));
        var tail = new StringBuilder();
        var gate = new object();
        async Task DrainAsync(StreamReader reader, StreamWriter? mirror = null)
        {
            var buffer = new char[4096];
            int count;
            while ((count = await reader.ReadAsync(buffer)) > 0)
            {
                lock (gate)
                {
                    writer.Write(buffer, 0, count);
                    mirror?.Write(buffer, 0, count);
                    tail.Append(buffer, 0, count);
                    if (tail.Length > 16000) tail.Remove(0, tail.Length - 16000);
                }
            }
        }

        var watch = Stopwatch.StartNew();
        cancellationToken.ThrowIfCancellationRequested();
        if (!process.Start()) throw new IOException("Native process did not start.");
        process.StandardInput.Close();
        using var job = ProcessJob.Attach(process);
        var stdout = DrainAsync(process.StandardOutput, stdoutWriter);
        var stderr = DrainAsync(process.StandardError);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Clamp(timeoutSeconds, 1, 300)));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, cancellationToken);
        var status = "completed";
        try
        {
            await process.WaitForExitAsync(linked.Token);
        }
        catch (OperationCanceledException)
        {
            status = cancellationToken.IsCancellationRequested ? "cancelled" : "timeout";
            job.Dispose();
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
        }
        finally
        {
            job.Dispose();
        }
        await Task.WhenAll(stdout, stderr);
        return new(status, process.ExitCode, tail.ToString(), logPath, watch.ElapsedMilliseconds, stdoutLogPath);
    }
}
