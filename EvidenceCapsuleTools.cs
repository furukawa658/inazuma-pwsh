using System.ComponentModel;
using ModelContextProtocol.Server;

namespace InazumaPwsh;

[McpServerToolType]
public sealed class EvidenceCapsuleTools
{
    private readonly EvidenceCapsuleStore _store;

    public EvidenceCapsuleTools(EvidenceCapsuleStore store)
    {
        _store = store;
    }

    [McpServerTool(Name = "store_capsule")]
    [Description("Store large evidence locally and return a compact capsule ID and summary. Pass sourcePath to avoid sending the full source body through the chat, or pass content when no file exists. Repeated identical content reuses the same capsule ID.")]
    public Task<string> StoreCapsuleAsync(
        [Description("Evidence type: file, command-output, test-report, or log.")] string kind,
        [Description("Optional evidence body. Specify either content or sourcePath, not both.")] string? content = null,
        [Description("Absolute path to a local source file. Prefer this over content for large evidence.")] string? sourcePath = null,
        [Description("Absolute working directory used to scope the evidence.")] string? workingDirectory = null,
        [Description("Optional short human-readable label, at most 200 characters.")] string? label = null,
        [Description("Maximum characters in the compact summary (80 to 4000).") ] int maxSummaryChars = 1000,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var directory = workingDirectory ?? Path.GetDirectoryName(sourcePath) ?? Directory.GetCurrentDirectory();
        var response = _store.Store(kind, content, directory, sourcePath, label, maxSummaryChars);
        UsageLog.RecordCall("store_capsule");
        return Task.FromResult(response);
    }

    [McpServerTool(Name = "query_capsule")]
    [Description("Read selected capsule lines without replaying content already returned by this server. Use baseCapsuleId for appended-data delta queries. Use startLine and endLine, or a regular expression query.")]
    public Task<string> QueryCapsuleAsync(
        [Description("Capsule ID returned by store_capsule.")] string capsuleId,
        [Description("Optional earlier capsule ID. If no explicit range is given, return only the appended delta.")] string? baseCapsuleId = null,
        [Description("Optional case-insensitive regular expression applied line by line.")] string? query = null,
        [Description("Optional 1-based first line to return.")] int? startLine = null,
        [Description("Optional inclusive 1-based last line to return.")] int? endLine = null,
        [Description("Omit lines already returned for this capsule in this server process.")] bool onlyUnseen = true,
        [Description("Maximum characters of selected line text to return (1000 to 50000).") ] int maxChars = 6000,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var response = _store.Query(capsuleId, baseCapsuleId, query, startLine, endLine, onlyUnseen, maxChars);
        UsageLog.RecordCall("query_capsule");
        return Task.FromResult(response);
    }
}
