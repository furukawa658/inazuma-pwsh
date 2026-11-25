using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace InazumaPwsh;

public sealed class SemanticGitDiff
{
    private const long MaxFileBytes = 2L * 1024 * 1024;

    public string Compare(string workingDirectory, string baseRef, string? path, int maxChanges, int maxChars)
    {
        if (string.IsNullOrWhiteSpace(workingDirectory) || !Directory.Exists(workingDirectory))
            return Error("workingDirectory must be an existing directory.");
        if (string.IsNullOrWhiteSpace(baseRef) || baseRef.StartsWith("-", StringComparison.Ordinal) || baseRef.Any(char.IsWhiteSpace))
            return Error("baseRef must be a valid Git revision name.");
        maxChanges = Math.Clamp(maxChanges, 1, 100);
        maxChars = Math.Clamp(maxChars, 500, 20000);

        var changed = RunGit(workingDirectory, "diff", "--name-only", "--no-renames", baseRef, "--", path ?? ".");
        if (changed.ExitCode != 0) return Error(changed.Error.Length > 0 ? changed.Error : "git diff failed.");
        var untracked = RunGit(workingDirectory, "ls-files", "--others", "--exclude-standard", "--", path ?? ".");
        if (untracked.ExitCode != 0) return Error(untracked.Error.Length > 0 ? untracked.Error : "git ls-files failed.");

        var paths = SplitLines(changed.Output).Concat(SplitLines(untracked.Output))
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Distinct(StringComparer.Ordinal)
            .Take(200)
            .ToList();
        var changes = new List<SemanticChange>();
        foreach (var relativePath in paths)
        {
            var currentPath = Path.GetFullPath(Path.Combine(workingDirectory, relativePath.Replace('/', Path.DirectorySeparatorChar)));
            if (!IsWithin(workingDirectory, currentPath)) continue;
            var before = RunGit(workingDirectory, "show", $"{baseRef}:{relativePath}");
            var beforeLines = before.ExitCode == 0 ? FileOutline.SplitLines(before.Output) : [];
            var afterLines = File.Exists(currentPath) && new FileInfo(currentPath).Length <= MaxFileBytes
                ? FileOutline.ReadAndDecodeFile(currentPath).Lines
                : [];
            if (beforeLines.Length == 0 && afterLines.Length == 0) continue;

            var beforeSymbols = GetSymbols(relativePath, beforeLines);
            var afterSymbols = GetSymbols(relativePath, afterLines);
            foreach (var symbol in beforeSymbols.Keys.Union(afterSymbols.Keys, StringComparer.Ordinal))
            {
                var existedBefore = beforeSymbols.TryGetValue(symbol, out var oldSymbol);
                var existsAfter = afterSymbols.TryGetValue(symbol, out var newSymbol);
                if (!existedBefore && existsAfter)
                    changes.Add(new("added", symbol, relativePath, null, newSymbol));
                else if (existedBefore && !existsAfter)
                    changes.Add(new("removed", symbol, relativePath, oldSymbol, null));
                else if (existedBefore && existsAfter && !oldSymbol!.Equals(newSymbol!))
                    changes.Add(new("modified", symbol, relativePath, oldSymbol, newSymbol));
                if (changes.Count >= maxChanges * 2) break;
            }
            if (changes.Count >= maxChanges * 2) break;
        }

        var bounded = changes.Take(maxChanges).Select(change => new
        {
            kind = change.Kind,
            symbol = change.Symbol,
            path = change.Path,
            oldLine = change.Before?.Start,
            newLine = change.After?.Start,
            oldSignature = change.Before?.Signature,
            newSignature = change.After?.Signature
        }).ToList();
        var result = JsonDefaults.Serialize(new
        {
            status = "success",
            baseRef,
            pathCount = paths.Count,
            changeCount = changes.Count,
            returnedChangeCount = bounded.Count,
            changes = bounded
        });
        if (result.Length <= maxChars) return result;
        var limitedChanges = bounded.Take(Math.Max(1, bounded.Count / 2)).ToList();
        return JsonDefaults.Serialize(new
        {
            status = "success",
            baseRef,
            pathCount = paths.Count,
            changeCount = changes.Count,
            returnedChangeCount = limitedChanges.Count,
            isTruncated = true,
            changes = limitedChanges
        });
    }

    public static string[] DiscoverChangedPaths(string workingDirectory)
    {
        var tracked = RunGit(workingDirectory, "diff", "--name-only", "HEAD", "--");
        var untracked = RunGit(workingDirectory, "ls-files", "--others", "--exclude-standard");
        return SplitLines(tracked.Output).Concat(SplitLines(untracked.Output))
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.Ordinal)
            .Take(200)
            .ToArray();
    }

    private static Dictionary<string, SymbolSnapshot> GetSymbols(string relativePath, string[] lines)
    {
        if (lines.Length == 0) return [];
        var snapshots = new Dictionary<string, SymbolSnapshot>(StringComparer.Ordinal);
        foreach (var entry in FileOutline.GetOutline(relativePath, lines))
        {
            var symbol = FileOutline.GetDeclaredSymbol(entry.Title);
            if (string.IsNullOrWhiteSpace(symbol)) continue;
            var end = Math.Clamp(FileOutline.GetDeclaredEnd(relativePath, lines, entry), entry.Start, lines.Length);
            var body = string.Join('\n', lines.Skip(entry.Start - 1).Take(end - entry.Start + 1));
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(body))).ToLowerInvariant()[..16];
            snapshots[symbol] = new SymbolSnapshot(entry.Start, end, NormalizeSignature(entry.Title), hash);
        }
        return snapshots;
    }

    private static string NormalizeSignature(string text) => Regex.Replace(text.Trim(), @"\s+", " ", RegexOptions.CultureInvariant);

    private static GitResult RunGit(string workingDirectory, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false)
        };
        startInfo.Environment["GIT_PAGER"] = "cat";
        startInfo.Environment["GIT_OPTIONAL_LOCKS"] = "0";
        startInfo.ArgumentList.Add("--no-pager");
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
        try
        {
            using var process = Process.Start(startInfo);
            if (process == null) return new GitResult(-1, string.Empty, "Unable to start git.");
            process.StandardInput.Close();
            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(5000))
            {
                process.Kill(true);
                process.WaitForExit(1000);
                return new GitResult(-1, string.Empty, "Git command timed out.");
            }
            return new GitResult(process.ExitCode, stdoutTask.GetAwaiter().GetResult(), stderrTask.GetAwaiter().GetResult().Trim());
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or IOException)
        {
            return new GitResult(-1, string.Empty, exception.Message);
        }
    }

    private static string[] SplitLines(string text) => text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static bool IsWithin(string root, string candidate)
    {
        var relative = Path.GetRelativePath(Path.GetFullPath(root), candidate);
        return relative != ".." && !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal);
    }

    private static string Error(string message) => JsonDefaults.Serialize(new { status = "error", message });

    private sealed record SymbolSnapshot(int Start, int End, string Signature, string BodyHash)
    {
        public bool Equals(SymbolSnapshot? other) => other != null && Signature == other.Signature && BodyHash == other.BodyHash;
        public override int GetHashCode() => HashCode.Combine(Signature, BodyHash);
    }

    private sealed record SemanticChange(string Kind, string Symbol, string Path, SymbolSnapshot? Before, SymbolSnapshot? After);
    private sealed record GitResult(int ExitCode, string Output, string Error);
}
