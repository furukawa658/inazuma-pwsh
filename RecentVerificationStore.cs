using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace InazumaPwsh;

internal static class RecentVerificationStore
{
    private const long MaxBytes = 1024L * 1024;
    private static readonly object Gate = new();
    private static readonly Regex SecretPattern = new(
        @"(?is)(bearer\s+[^\s,;]+|(?:api[_-]?key|password|passwd|secret)\s*[:=]\s*[^\s,;]+|https?://[^\s?]+\?[^\s]+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static void Record(string tool, string workingDirectory, string report)
    {
        try
        {
            var root = JsonNode.Parse(report)?.AsObject();
            if (root == null) return;
            var failures = root["failures"] as JsonArray;
            var diagnostics = root["diagnostics"] as JsonArray;
            var errors = root["errors"] as JsonArray;
            var primary = failures?.OfType<JsonObject>().FirstOrDefault()
                ?? diagnostics?.OfType<JsonObject>().FirstOrDefault()
                ?? errors?.OfType<JsonObject>().FirstOrDefault();
            var entry = new JsonObject
            {
                ["time"] = DateTimeOffset.UtcNow,
                ["tool"] = tool,
                ["workspace"] = Path.GetFullPath(workingDirectory),
                ["status"] = root["status"]?.GetValue<string>() ?? "unknown",
                ["failureCount"] = failures?.Count ?? (root["stats"]?["failed"]?.GetValue<int>() ?? errors?.Count ?? diagnostics?.Count ?? 0),
                ["fullLogPath"] = root["fullLogPath"]?.GetValue<string>(),
                ["fullReportPath"] = root["fullReportPath"]?.GetValue<string>()
            };
            if (primary != null)
            {
                entry["firstFailure"] = new JsonObject
                {
                    ["file"] = Redact(primary["file"]?.GetValue<string>()),
                    ["line"] = primary["line"]?.GetValue<int?>(),
                    ["message"] = Redact(primary["message"]?.GetValue<string>())
                };
            }
            Append(entry.ToJsonString(JsonDefaults.Options));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException) { }
    }

    private static string? Redact(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return value;
        return SecretPattern.Replace(value[..Math.Min(value.Length, 300)], "[REDACTED]");
    }

    private static void Append(string json)
    {
        lock (Gate)
        {
            var directory = Path.Combine(Path.GetTempPath(), "inazuma-pwsh-logs");
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "verification-history.jsonl");
            if (File.Exists(path) && new FileInfo(path).Length > MaxBytes) File.Delete(path);
            File.AppendAllText(path, json + Environment.NewLine);
        }
    }
}
