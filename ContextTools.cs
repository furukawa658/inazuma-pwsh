using System.ComponentModel;
using System.Text.RegularExpressions;
using ModelContextProtocol.Server;

namespace InazumaPwsh;

[McpServerToolType]
public sealed class ContextTools
{
    private const int MaxPlannedFiles = 200;
    private readonly ContextStateStore _context;
    private readonly SemanticGitDiff _semanticDiff;

    public ContextTools(ContextStateStore context, SemanticGitDiff semanticDiff)
    {
        _context = context;
        _semanticDiff = semanticDiff;
    }

    [McpServerTool(Name = "semantic_git_diff")]
    [Description("Compare the Git worktree with a revision and return compact declaration-level additions, removals, and modifications instead of a raw text diff.")]
    public Task<string> SemanticGitDiffAsync(
        [Description("Absolute path to the Git working directory.")] string workingDirectory,
        [Description("Git revision to compare against. Defaults to HEAD.")] string baseRef = "HEAD",
        [Description("Optional repository-relative path or directory.")] string? path = null,
        [Description("Maximum semantic changes to return (1 to 100).")] int maxChanges = 30,
        [Description("Maximum serialized response characters (500 to 20000).")] int maxChars = 6000,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var result = _semanticDiff.Compare(workingDirectory, baseRef, path, maxChanges, maxChars);
        UsageLog.RecordCall("semantic_git_diff");
        return Task.FromResult(result);
    }

    [McpServerTool(Name = "remember_context")]
    [Description("Persist one compact fact, decision, unknown, next action, or invalidation marker in the local context store. Reusing a kind and key deactivates the older record.")]
    public Task<string> RememberContextAsync(
        [Description("Absolute workspace path whose context is stored.")] string workingDirectory,
        [Description("fact, decision, unknown, next-action, or invalidated.")] string kind,
        [Description("Stable context key, at most 200 characters.")] string key,
        [Description("Compact context value, at most 12000 characters.")] string value,
        [Description("Optional evidence reference such as a file path or capsule ID.")] string? evidence = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var result = _context.Remember(workingDirectory, kind, key, value, evidence);
        UsageLog.RecordCall("remember_context");
        return Task.FromResult(result);
    }

    [McpServerTool(Name = "query_context")]
    [Description("Query the local fact store and return only active structured facts, decisions, unknowns, next actions, and invalidation markers that match the request.")]
    public Task<string> QueryContextAsync(
        [Description("Absolute workspace path whose context is queried.")] string workingDirectory,
        [Description("Optional case-insensitive words to match in keys, values, or evidence.")] string? query = null,
        [Description("Optional kinds to include: fact, decision, unknown, next-action, invalidated.")] string[]? kinds = null,
        [Description("Return active records only. Defaults to true.")] bool onlyActive = true,
        [Description("Maximum matching records (1 to 50).")] int maxResults = 12,
        [Description("Maximum response characters (500 to 12000).")] int maxChars = 4000,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var result = _context.Query(workingDirectory, query, kinds, onlyActive, maxResults, maxChars);
        UsageLog.RecordCall("query_context");
        return Task.FromResult(result);
    }

    [McpServerTool(Name = "compile_context")]
    [Description("Compile the active local facts for a workspace into a bounded Goal, Facts, Decisions, Unknowns, NextAction, Invalidated, and Evidence representation.")]
    public Task<string> CompileContextAsync(
        [Description("Absolute workspace path whose context is compiled.")] string workingDirectory,
        [Description("Current task goal.")] string goal,
        [Description("Maximum compiled characters (500 to 12000).")] int maxChars = 4000,
        [Description("Include evidence references without loading their content.")] bool includeEvidence = true,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var result = _context.Compile(workingDirectory, goal, includeEvidence, maxChars);
        UsageLog.RecordCall("compile_context");
        return Task.FromResult(result);
    }

    [McpServerTool(Name = "plan_context_read")]
    [Description("Plan the smallest next read for a question by ranking changed files, outline symbols, source terms, and local dependencies before any source body is sent to the model.")]
    public Task<string> PlanContextReadAsync(
        [Description("Absolute workspace path to inspect.")] string workingDirectory,
        [Description("Question or task statement used to rank source locations.")] string question,
        [Description("Optional repository-relative candidate files. When omitted, changed code files are prioritized.")] string[]? paths = null,
        [Description("Maximum recommendations (1 to 12).")] int maxRecommendations = 5,
        [Description("Maximum serialized response characters (500 to 12000).")] int maxChars = 4000,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var result = PlanRead(workingDirectory, question, paths, maxRecommendations, maxChars, cancellationToken);
        UsageLog.RecordCall("plan_context_read");
        return Task.FromResult(result);
    }

    private static string PlanRead(string workingDirectory, string question, string[]? paths, int maxRecommendations, int maxChars, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(workingDirectory) || !Directory.Exists(workingDirectory))
            return Error("workingDirectory must be an existing directory.");
        if (string.IsNullOrWhiteSpace(question) || question.Length > 2000)
            return Error("question must contain 1 to 2000 characters.");
        maxRecommendations = Math.Clamp(maxRecommendations, 1, 12);
        maxChars = Math.Clamp(maxChars, 500, 12000);
        var terms = Tokenize(question);
        var changed = paths == null ? SemanticGitDiff.DiscoverChangedPaths(workingDirectory) : [];
        var candidates = (paths ?? changed)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.Ordinal)
            .Take(MaxPlannedFiles)
            .ToList();
        if (candidates.Count == 0)
            candidates = EnumerateSourceFiles(workingDirectory).Take(MaxPlannedFiles).ToList();

        var ranked = new List<ReadRecommendation>();
        foreach (var relativePath in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var absolutePath = Path.GetFullPath(Path.Combine(workingDirectory, relativePath.Replace('/', Path.DirectorySeparatorChar)));
            if (!IsWithin(workingDirectory, absolutePath) || !File.Exists(absolutePath)) continue;
            var fileInfo = new FileInfo(absolutePath);
            if (fileInfo.Length > 2L * 1024 * 1024 || FileOutline.DetermineKind(absolutePath) == "text") continue;
            var decoded = FileOutline.ReadAndDecodeFile(absolutePath);
            var entries = FileOutline.GetOutline(absolutePath, decoded.Lines);
            var matchingEntries = entries
                .Select(entry => new
                {
                    Entry = entry,
                    Score = Score(entry.Title, terms) + Score(string.Join('\n', decoded.Lines.Skip(entry.Start - 1).Take(Math.Clamp(entry.End - entry.Start + 1, 1, 80))), terms)
                })
                .OrderByDescending(item => item.Score)
                .ThenBy(item => item.Entry.Start)
                .ToList();
            var best = matchingEntries.FirstOrDefault();
            var score = Score(relativePath, terms) + (best?.Score ?? 0) + (changed.Contains(relativePath, StringComparer.Ordinal) ? 5 : 0);
            if (score == 0 && candidates.Count > maxRecommendations) continue;
            var symbols = entries.Select(entry => FileOutline.GetDeclaredSymbol(entry.Title))
                .Where(symbol => symbol != null && terms.Any(term => symbol.Contains(term, StringComparison.OrdinalIgnoreCase)))
                .Select(symbol => symbol!)
                .Distinct(StringComparer.Ordinal)
                .Take(12)
                .ToArray();
            var selected = best?.Entry;
            var dependencyPaths = FindDependencies(workingDirectory, absolutePath, decoded.Lines);
            ranked.Add(new ReadRecommendation(
                relativePath.Replace('\\', '/'),
                score,
                symbols.Length > 0 ? "read_symbol" : "read_section",
                selected?.Start ?? 1,
                selected == null ? Math.Min(decoded.Lines.Length, 80) : selected.End,
                symbols,
                selected?.Title,
                dependencyPaths,
                EstimateChars(selected, decoded.Lines.Length)));
        }

        var recommendations = ranked.OrderByDescending(item => item.Score)
            .ThenBy(item => item.Path, StringComparer.Ordinal)
            .Take(maxRecommendations)
            .ToList();
        var result = JsonDefaults.Serialize(new
        {
            status = "success",
            strategy = "outline-first",
            recommendation = recommendations.FirstOrDefault() == null
                ? null
                : new
                {
                    tool = recommendations[0].SuggestedTool,
                    recommendations[0].Path,
                    query = recommendations[0].Title ?? question,
                    recommendations[0].StartLine,
                    recommendations[0].EndLine
                },
            recommendations = recommendations.Select(item => new
            {
                path = item.Path,
                score = item.Score,
                suggestedTool = item.SuggestedTool,
                startLine = item.StartLine,
                endLine = item.EndLine,
                symbols = item.Symbols,
                outlineTitle = item.Title,
                dependencyPaths = item.DependencyPaths,
                estimatedChars = item.EstimatedChars
            }),
            maxChars
        });
        if (result.Length <= maxChars) return result;
        var compact = recommendations.Take(Math.Max(1, recommendations.Count / 2)).Select(item => new
        {
            path = item.Path,
            suggestedTool = item.SuggestedTool,
            startLine = item.StartLine,
            endLine = item.EndLine,
            symbols = item.Symbols,
            dependencyPaths = item.DependencyPaths
        });
        return JsonDefaults.Serialize(new
        {
            status = "success",
            strategy = "outline-first",
            isTruncated = true,
            recommendations = compact,
            maxChars
        });
    }

    private static int Score(string text, IReadOnlyCollection<string> terms) => terms.Sum(term => Regex.Matches(text, Regex.Escape(term), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant).Count);

    private static List<string> Tokenize(string question) => Regex.Split(question.ToLowerInvariant(), @"\W+", RegexOptions.CultureInvariant)
        .Where(token => token.Length >= 3)
        .Distinct(StringComparer.Ordinal)
        .Take(30)
        .ToList();

    private static IEnumerable<string> EnumerateSourceFiles(string workingDirectory)
    {
        var stack = new Stack<string>([workingDirectory]);
        var excluded = new HashSet<string>([".git", "node_modules", "bin", "obj", ".venv", "dist", "target"], StringComparer.OrdinalIgnoreCase);
        var resultCount = 0;
        while (stack.Count > 0 && resultCount < 1000)
        {
            var directory = stack.Pop();
            foreach (var child in Directory.EnumerateDirectories(directory))
                if (!excluded.Contains(Path.GetFileName(child))) stack.Push(child);
            foreach (var file in Directory.EnumerateFiles(directory))
            {
                if (FileOutline.DetermineKind(file) == "text") continue;
                if (Path.GetExtension(file).Equals(".json", StringComparison.OrdinalIgnoreCase)) continue;
                resultCount++;
                yield return Path.GetRelativePath(workingDirectory, file);
                if (resultCount >= 1000) yield break;
            }
        }
    }

    private static string[] FindDependencies(string root, string sourcePath, string[] lines)
    {
        var dependencies = new HashSet<string>(StringComparer.Ordinal);
        var directory = Path.GetDirectoryName(sourcePath)!;
        foreach (var line in lines)
        {
            foreach (var match in Regex.Matches(line, @"(?:from\s+|require\(|import\s+)[\""'](?<path>\.[^\""']+)[\""']", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant).Cast<Match>())
            {
                var candidate = Path.GetFullPath(Path.Combine(directory, match.Groups["path"].Value));
                foreach (var resolved in ResolveDependency(candidate))
                {
                    if (IsWithin(root, resolved) && File.Exists(resolved)) dependencies.Add(Path.GetRelativePath(root, resolved).Replace('\\', '/'));
                }
            }
        }
        return dependencies.Take(12).ToArray();
    }

    private static IEnumerable<string> ResolveDependency(string basePath)
    {
        yield return basePath;
        foreach (var extension in new[] { ".ts", ".tsx", ".js", ".jsx", ".mjs", ".cjs" }) yield return basePath + extension;
        if (Directory.Exists(basePath))
            foreach (var extension in new[] { "index.ts", "index.tsx", "index.js", "index.mjs" }) yield return Path.Combine(basePath, extension);
    }

    private static int EstimateChars(OutlineEntry? entry, int lineCount)
    {
        var lines = entry == null ? Math.Min(80, lineCount) : Math.Clamp(entry.End - entry.Start + 1, 1, 200);
        return lines * 80;
    }

    private static bool IsWithin(string root, string candidate)
    {
        var relative = Path.GetRelativePath(Path.GetFullPath(root), candidate);
        return relative != ".." && !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal);
    }

    private static string Error(string message) => JsonDefaults.Serialize(new { status = "error", message });

    private sealed record ReadRecommendation(string Path, int Score, string SuggestedTool, int StartLine, int EndLine, string[] Symbols, string? Title, string[] DependencyPaths, int EstimatedChars);
}
