using System.Text;
using System.Text.Json.Nodes;

namespace InazumaPwsh;

/// <summary>Adds source lines around each failure or diagnostic location to a parsed report.</summary>
internal static class CodeContext
{
    public const int MaxContextLines = 10;
    private const int TotalBudget = 2400;
    private const int MaxLineChars = 160;
    private const long MaxFileBytes = 2L * 1024 * 1024;
    private static readonly string[] ExcludedSegments = ["node_modules", ".venv", "site-packages"];
    private static readonly string[] ItemArrays = ["failures", "diagnostics", "errors"];

    public static string Add(string report, string workingDirectory, int contextLines)
    {
        var radius = Math.Clamp(contextLines, 0, MaxContextLines);
        if (radius == 0) return report;
        try
        {
            if (JsonNode.Parse(report) is not JsonObject root) return report;
            var remaining = TotalBudget;
            var realRoot = RealPath(workingDirectory);
            var shown = new Dictionary<string, HashSet<int>>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
            foreach (var name in ItemArrays)
            {
                if (root[name] is not JsonArray items) continue;
                foreach (var item in items.OfType<JsonObject>())
                {
                    if (remaining <= 0) break;
                    var context = Read(item, workingDirectory, realRoot, radius, remaining, shown);
                    if (context == null) continue;
                    item["codeContext"] = context;
                    remaining -= context.Length;
                }
            }
            return root.ToJsonString(JsonDefaults.Options);
        }
        catch (Exception exception) when (exception is System.Text.Json.JsonException or IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return report;
        }
    }

    // Compilers report resolved paths, so /var and /private/var on macOS must compare as equal.
    private static string RealPath(string path)
    {
        var current = Path.GetPathRoot(path) ?? "";
        foreach (var segment in path[current.Length..].Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            try
            {
                if (new DirectoryInfo(current).LinkTarget != null && Directory.ResolveLinkTarget(current, true) is { } target) current = target.FullName;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        }
        return current;
    }

    // A line already returned for an earlier item of the same file is skipped, except the item's own line.
    private static string? Read(JsonObject item, string workingDirectory, string realRoot, int radius, int budget, Dictionary<string, HashSet<int>> shown)
    {
        var file = (item["file"] ?? item["path"])?.GetValue<string>();
        var line = item["line"]?.GetValue<int>() ?? 0;
        if (string.IsNullOrEmpty(file) || line <= 0) return null;
        var path = RealPath(Path.GetFullPath(file, workingDirectory));
        var relative = Path.GetRelativePath(realRoot, path);
        if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)) return null;
        var segments = path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (segments.Any(segment => ExcludedSegments.Contains(segment, StringComparer.OrdinalIgnoreCase))) return null;
        var info = new FileInfo(path);
        if (!info.Exists || info.Length > MaxFileBytes) return null;

        var (lines, _, _) = FileOutline.ReadAndDecodeFile(path);
        if (line > lines.Length) return null;
        var first = Math.Max(1, line - radius);
        var last = Math.Min(lines.Length, line + radius);
        var width = last.ToString().Length;
        if (!shown.TryGetValue(relative, out var seen)) shown[relative] = seen = [];
        var builder = new StringBuilder();
        for (var number = first; number <= last; number++)
        {
            if (number != line && seen.Contains(number)) continue;
            var text = lines[number - 1].TrimEnd();
            if (text.Length > MaxLineChars) text = text[..MaxLineChars] + "...";
            var entry = $"{(number == line ? '>' : ' ')}{number.ToString().PadLeft(width)}| {text}\n";
            if (builder.Length + entry.Length > budget) break;
            builder.Append(entry);
            seen.Add(number);
        }
        return builder.Length == 0 ? null : builder.ToString().TrimEnd('\n');
    }
}
