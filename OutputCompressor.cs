using System.Text;
using System.Text.RegularExpressions;

namespace InazumaPwsh;

public static partial class OutputCompressor
{
    [GeneratedRegex(@"\x1B(?:\[[0-?]*[ -/]*[@-~]|\][^\x07\x1B]*(?:\x07|\x1B\\))")]
    private static partial Regex AnsiRegex();

    [GeneratedRegex(@"\b(error|CS\d{4}|MSB\d{4}|TS\d{4}|failed|failure|exception|fatal)\b", RegexOptions.IgnoreCase)]
    private static partial Regex SignalRegex();

    public sealed record CompressedResult(string Content, bool IsTruncated, int OriginalChars,
        int OriginalLines, string? FullLogPath);

    public static async Task<CompressedResult> CompressAsync(string rawOutput, int maxChars = 6000)
    {
        maxChars = Math.Clamp(maxChars, 1000, 50000);
        var sanitized = AnsiRegex().Replace(rawOutput, "");
        var lines = sanitized.Replace("\r\n", "\n").Split('\n');
        if (sanitized.Length <= maxChars)
            return new(sanitized, false, sanitized.Length, lines.Length, null);

        var directory = Path.Combine(Path.GetTempPath(), "inazuma-pwsh-logs");
        Directory.CreateDirectory(directory);
        var logPath = Path.Combine(directory, $"{Guid.NewGuid():N}.log");
        await File.WriteAllTextAsync(logPath, sanitized, new UTF8Encoding(false));

        var signals = new StringBuilder();
        var signalBudget = maxChars / 2;
        var included = new SortedSet<int>();
        for (var index = lines.Length - 1; index >= 0; index--)
        {
            if (!SignalRegex().IsMatch(lines[index])) continue;
            for (var nearby = Math.Max(0, index - 1); nearby <= Math.Min(lines.Length - 1, index + 2); nearby++)
                included.Add(nearby);
        }
        foreach (var index in included.Reverse())
        {
            var prefix = $"L{index + 1}: ";
            var available = signalBudget - signals.Length - prefix.Length - 1;
            if (available <= 0) break;
            signals.Append(prefix).Append(lines[index].AsSpan(0, Math.Min(lines[index].Length, available))).Append('\n');
        }
        var header = $"[Output truncated; full log: {logPath}]\n{signals}\n[HEAD / TAIL]\n";
        var budget = Math.Max(0, maxChars - header.Length - 24);
        var headLength = budget / 5;
        var tailLength = budget - headLength;
        var content = header + sanitized[..headLength] + "\n[... omitted ...]\n" + sanitized[^tailLength..];
        return new(content[..Math.Min(content.Length, maxChars)], true, sanitized.Length, lines.Length, logPath);
    }
}