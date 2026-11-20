using System.Text.Json.Nodes;

namespace InazumaPwsh;

internal static class TestRegressionFilter
{
    internal const string PassedTestsProperty = "passedTests";

    public static string Apply(string report, IReadOnlySet<string>? previous, bool regressionsOnly, out HashSet<string>? current)
    {
        if (JsonNode.Parse(report) is not JsonObject root)
        {
            current = null;
            return report;
        }

        var hasParsedPassedTests = root[PassedTestsProperty] is JsonArray;
        var passed = root[PassedTestsProperty] is JsonArray array
            ? array.Select(value => value?.GetValue<string>()).Where(value => value != null).Select(value => value!).ToHashSet(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var status = root["status"]?.GetValue<string>();
        var valid = status is "success" or "no_tests" || status == "error" && hasParsedPassedTests;
        current = valid ? passed : null;
        root.Remove(PassedTestsProperty);

        if (regressionsOnly && previous != null && root["failures"] is JsonArray failures)
        {
            var kept = failures.OfType<JsonObject>().Where(failure =>
            {
                var exact = Identity(failure);
                if (previous.Contains(exact)) return true;
                var titleOnly = Identity(null, failure["projectName"]?.GetValue<string>(), failure["title"]?.GetValue<string>());
                return previous.Contains(titleOnly);
            }).ToArray();
            failures.Clear();
            foreach (var failure in kept) failures.Add(failure);
            root["regressionCount"] = kept.Length;
        }

        return root.ToJsonString(JsonDefaults.Options);
    }

    public static string Identity(string? file, string? project, string? title) =>
        $"{file}|{project}|{title}";

    private static string Identity(JsonObject failure) => Identity(
        failure["file"]?.GetValue<string>(), failure["projectName"]?.GetValue<string>(), failure["title"]?.GetValue<string>());
}
