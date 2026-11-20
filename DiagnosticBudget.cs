using System.Text.Json.Nodes;

namespace InazumaPwsh;

internal static class DiagnosticBudget
{
    private const int MaxSummaryChars = 240;

    public static string Fit(string report, int maxChars, params string[] arrayNames)
    {
        if (JsonNode.Parse(report) is not JsonObject root) return report;
        return Fit(root, maxChars, arrayNames);
    }

    public static string Fit(JsonObject root, int maxChars, params string[] arrayNames)
    {
        var json = root.ToJsonString(JsonDefaults.Options);
        if (json.Length <= maxChars) return json;

        foreach (var arrayName in arrayNames)
        {
            if (root[arrayName] is not JsonArray array) continue;
            for (var index = 1; index < array.Count; index++)
                array[index] = array[index] is JsonValue stringValue && stringValue.TryGetValue<string>(out var text)
                    ? CompactText(text)
                    : Summarize(array[index] as JsonObject);
        }

        json = root.ToJsonString(JsonDefaults.Options);
        if (json.Length <= maxChars) return json;

        foreach (var arrayName in arrayNames)
        {
            if (root[arrayName] is not JsonArray array || array.Count <= 1) continue;
            var omitted = 0;
            while (array.Count > 1 && root.ToJsonString(JsonDefaults.Options).Length > maxChars)
            {
                array.RemoveAt(array.Count - 1);
                omitted++;
            }
            if (omitted > 0) root[$"omitted{Pascal(arrayName)}"] = omitted;
            json = root.ToJsonString(JsonDefaults.Options);
            if (json.Length <= maxChars) return json;
        }

        return json;
    }

    private static JsonObject Summarize(JsonObject? item)
    {
        if (item == null) return new JsonObject { ["summary"] = "Additional diagnostic" };

        var summary = new JsonObject();
        string[] fields = ["file", "path", "line", "column", "title", "status", "code", "ruleId", "rule", "severity", "projectName", "count"];
        foreach (var field in fields)
            if (item[field] is JsonNode value) summary[field] = value.DeepClone();

        var file = ReadString(item, "file") ?? ReadString(item, "path") ?? "";
        var line = ReadInt(item, "line");
        var location = file.Length > 0 ? line > 0 ? $"{file}:{line}" : file : "";
        var label = ReadString(item, "title") ?? ReadString(item, "code") ?? ReadString(item, "ruleId") ?? ReadString(item, "rule") ?? "";
        var message = ReadString(item, "message");
        if (message == null && item["errors"] is JsonArray errors)
            message = errors.OfType<JsonObject>().Select(error => ReadString(error, "message")).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
        var text = string.Join(": ", new[] { location, label, message }.Where(value => !string.IsNullOrWhiteSpace(value)));
        summary["summary"] = CompactText(text.Length > 0 ? text : "Additional diagnostic");
        return summary;
    }

    private static string? ReadString(JsonObject item, string name) => item[name]?.GetValue<string>();

    private static int ReadInt(JsonObject item, string name)
    {
        try { return item[name]?.GetValue<int>() ?? 0; }
        catch (InvalidOperationException) { return 0; }
    }

    private static string CompactText(string text)
    {
        var normalized = string.Join(' ', text.Replace('\r', ' ').Replace('\n', ' ').Split(' ', StringSplitOptions.RemoveEmptyEntries));
        return PlaywrightResultParser.Truncate(normalized, MaxSummaryChars);
    }

    private static string Pascal(string value) => value.Length == 0 ? value : char.ToUpperInvariant(value[0]) + value[1..];
}
