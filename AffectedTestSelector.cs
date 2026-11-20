using System.Text.RegularExpressions;
using System.Text.Json;

namespace InazumaPwsh;

internal static partial class AffectedTestSelector
{
    private static readonly HashSet<string> IgnoredDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", "node_modules", "bin", "obj", "dist", "coverage", ".venv", "__pycache__"
    };

    [GeneratedRegex("(?:from\\s+[\"']|import\\s+(?:[^;\\r\\n]+?\\s+from\\s+)?[\"']|require\\(\\s*[\"'])(?<module>[^\"']+)[\"']", RegexOptions.Compiled)]
    private static partial Regex ImportRegex();

    public sealed record Selection(string File, string Runner, string ProjectDirectory);

    public static async Task<(string RepositoryRoot, string[] ChangedFiles, List<Selection> Tests)> SelectAsync(
        string workingDirectory, CancellationToken cancellationToken)
    {
        var root = (await RunGitAsync(workingDirectory, ["rev-parse", "--show-toplevel"], cancellationToken)).Trim();
        var changed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in SplitNull(await RunGitAsync(root, ["diff", "--name-only", "--diff-filter=ACMR", "-z", "HEAD", "--"], cancellationToken)))
            changed.Add(Normalize(file));
        foreach (var file in SplitNull(await RunGitAsync(root, ["ls-files", "--others", "--exclude-standard", "-z"], cancellationToken)))
            changed.Add(Normalize(file));

        var changedFiles = changed.Where(file => file.Length > 0).Order(StringComparer.Ordinal).ToArray();
        var tracked = (await RunGitAsync(root, ["ls-files", "-co", "--exclude-standard", "-z"], cancellationToken))
            .Split('\0', StringSplitOptions.RemoveEmptyEntries)
            .Select(Normalize)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var candidates = tracked.Where(IsTestFile).ToArray();
        var selected = new List<Selection>();
        foreach (var candidate in candidates)
        {
            var fullCandidate = Path.Combine(root, candidate.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(fullCandidate)) continue;
            var direct = changed.Contains(candidate);
            var nameMatch = changed.Any(changedFile => NameMatches(changedFile, candidate));
            var importMatch = changed.Any(changedFile => Imports(candidate, fullCandidate, changedFile, root));
            if (!direct && !nameMatch && !importMatch) continue;
            var runner = DetectRunner(fullCandidate, root);
            if (runner == null) continue;
            var projectDirectory = FindProjectDirectory(fullCandidate, root, runner);
            selected.Add(new Selection(candidate, runner, projectDirectory));
        }

        return (root, changedFiles, selected.OrderBy(item => item.File, StringComparer.Ordinal).ToList());
    }

    private static async Task<string> RunGitAsync(string workingDirectory, string[] arguments, CancellationToken cancellationToken)
    {
        var gitArguments = new[] { "-c", "core.safecrlf=false", "-c", "core.autocrlf=false" }.Concat(arguments);
        var execution = await NativeProcessRunner.RunAsync("git", gitArguments, workingDirectory, 20, null, cancellationToken);
        try
        {
            var output = await File.ReadAllTextAsync(execution.FullLogPath, cancellationToken);
            if (execution.Status != "completed") throw new IOException($"git {string.Join(' ', arguments)} ended with status {execution.Status}.");
            if (execution.ExitCode != 0) throw new IOException(output.Trim().Length > 0 ? output.Trim() : $"git exited with code {execution.ExitCode}.");
            return output;
        }
        finally
        {
            try { File.Delete(execution.FullLogPath); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private static IEnumerable<string> SplitNull(string value) => value.Split('\0', StringSplitOptions.RemoveEmptyEntries);

    private static string Normalize(string path) => path.Replace('\\', '/').TrimStart('/');

    private static bool IsTestFile(string relativePath)
    {
        var path = Normalize(relativePath);
        if (path.Split('/').Any(IgnoredDirectories.Contains)) return false;
        var extension = Path.GetExtension(path);
        var name = Path.GetFileName(path);
        return (extension.Equals(".cs", StringComparison.OrdinalIgnoreCase) && Regex.IsMatch(name, @"Tests?\.cs$", RegexOptions.IgnoreCase))
            || (extension.Equals(".py", StringComparison.OrdinalIgnoreCase) && (name.StartsWith("test_", StringComparison.OrdinalIgnoreCase) || name.EndsWith("_test.py", StringComparison.OrdinalIgnoreCase)))
            || Regex.IsMatch(name, @"\.(?:test|spec)\.(?:[cm]?[jt]sx?|tsx?)$", RegexOptions.IgnoreCase);
    }

    private static bool NameMatches(string changedFile, string testFile)
    {
        var changedStem = Path.GetFileNameWithoutExtension(changedFile);
        var testStem = Path.GetFileNameWithoutExtension(testFile);
        testStem = Regex.Replace(testStem, @"(?:\.test|\.spec|_test|Tests?)$", "", RegexOptions.IgnoreCase);
        if (testStem.StartsWith("test_", StringComparison.OrdinalIgnoreCase)) testStem = testStem[5..];
        return changedStem.Equals(testStem, StringComparison.OrdinalIgnoreCase);
    }

    private static bool Imports(string candidate, string fullCandidate, string changedFile, string root)
    {
        string text;
        try
        {
            if (new FileInfo(fullCandidate).Length > 2L * 1024 * 1024) return false;
            text = File.ReadAllText(fullCandidate);
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }

        foreach (Match match in ImportRegex().Matches(text))
        {
            var module = match.Groups["module"].Value.Replace('\\', '/');
            if (!module.StartsWith('.') || Path.IsPathRooted(module)) continue;
            var candidateDirectory = Path.GetDirectoryName(fullCandidate)!;
            var resolved = Path.GetFullPath(Path.Combine(candidateDirectory, module));
            if (ResolvesToFile(resolved, Path.Combine(root, changedFile.Replace('/', Path.DirectorySeparatorChar)))) return true;
        }

        return false;
    }

    private static bool ResolvesToFile(string modulePath, string changedPath)
    {
        var candidates = new List<string> { modulePath, modulePath + ".ts", modulePath + ".tsx", modulePath + ".js", modulePath + ".jsx", modulePath + ".mjs", modulePath + ".cjs", modulePath + ".py" };
        foreach (var extension in new[] { ".ts", ".tsx", ".js", ".jsx", ".mjs", ".cjs", ".py" })
            candidates.Add(Path.Combine(modulePath, "index" + extension));
        return candidates.Any(path => string.Equals(Path.GetFullPath(path), Path.GetFullPath(changedPath), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal));
    }

    private static string? DetectRunner(string fullTestPath, string root)
    {
        var extension = Path.GetExtension(fullTestPath);
        if (extension.Equals(".py", StringComparison.OrdinalIgnoreCase)) return "pytest";
        if (extension.Equals(".cs", StringComparison.OrdinalIgnoreCase)) return "dotnet";
        string text = "";
        try { text = File.ReadAllText(fullTestPath); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        if (text.Contains("@playwright/test", StringComparison.Ordinal)) return "playwright";
        if (text.Contains("from 'bun:test'", StringComparison.Ordinal) || text.Contains("from \"bun:test\"", StringComparison.Ordinal)) return "bun";
        if (text.Contains("from 'vitest'", StringComparison.Ordinal) || text.Contains("from \"vitest\"", StringComparison.Ordinal) || HasPackageDependency(fullTestPath, root, "vitest")) return "vitest";
        return null;
    }

    private static string FindProjectDirectory(string fullTestPath, string root, string runner)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (runner == "dotnet")
            for (var directory = new DirectoryInfo(Path.GetDirectoryName(fullTestPath)!); directory != null; directory = directory.Parent)
            {
                var projects = directory.GetFiles("*.csproj");
                if (projects.Length == 1) return directory.FullName;
                if (projects.Length > 1) throw new IOException($"Multiple .csproj files were found at {directory.FullName}; select an unambiguous test project layout.");
                if (Path.GetFullPath(directory.FullName).Equals(Path.GetFullPath(root), comparison)) break;
            }
        for (var directory = new DirectoryInfo(Path.GetDirectoryName(fullTestPath)!); directory != null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "package.json"))) return directory.FullName;
            if (Path.GetFullPath(directory.FullName).Equals(Path.GetFullPath(root), comparison)) break;
        }
        return root;
    }

    private static bool HasPackageDependency(string file, string root, string package)
    {
        for (var directory = new DirectoryInfo(Path.GetDirectoryName(file)!); directory != null; directory = directory.Parent)
        {
            var packagePath = Path.Combine(directory.FullName, "package.json");
            if (File.Exists(packagePath))
                try
                {
                    using var document = JsonDocument.Parse(File.ReadAllText(packagePath));
                    foreach (var section in new[] { "dependencies", "devDependencies" })
                        if (document.RootElement.TryGetProperty(section, out var values) && values.TryGetProperty(package, out _)) return true;
                }
                catch (JsonException) { }
                catch (IOException) { }
            if (Path.GetFullPath(directory.FullName) == Path.GetFullPath(root)) break;
        }
        return false;
    }
}
