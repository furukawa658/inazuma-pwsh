using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace InazumaPwsh;

internal static class RedundantReadGuard
{
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(5);
    private const int MaxEntries = 2000;
    private static readonly ConcurrentDictionary<string, Entry> Entries = new(StringComparer.Ordinal);

    private sealed record Entry(DateTimeOffset ReturnedAt, int ResponseChars);

    public static async Task<string> TrackAsync(string tool, string path, object request, Func<Task<string>> action)
    {
        var file = TryGetFile(path);
        if (file == null) return await action();

        var normalizedPath = Path.GetFullPath(path);
        var fingerprint = $"{file.Length}:{file.LastWriteTimeUtc.Ticks}";
        var requestText = JsonDefaults.Serialize(new
        {
            tool,
            path = normalizedPath,
            fingerprint,
            request
        });
        var requestHash = Hash(requestText);
        var now = DateTimeOffset.UtcNow;

        if (Entries.TryGetValue(requestHash, out var previous))
        {
            if (now - previous.ReturnedAt <= Ttl)
            {
                return JsonDefaults.Serialize(new
                {
                    status = "duplicate",
                    duplicate = true,
                    tool,
                    path = normalizedPath,
                    requestHash,
                    previousReturnedAt = previous.ReturnedAt.ToUnixTimeMilliseconds(),
                    omittedResponseChars = previous.ResponseChars,
                    message = "This exact read was already returned from the same file version. The original response remains in context.",
                    hint = "Change the requested range or arguments to read again. Any source-file change automatically invalidates this marker."
                });
            }

            Entries.TryRemove(requestHash, out _);
        }

        var response = await action();
        if (IsSuccess(response)) Entries[requestHash] = new Entry(now, response.Length);
        TrimExpiredAndBounded(now);
        return response;
    }

    private static FileInfo? TryGetFile(string path)
    {
        try
        {
            if (!Path.IsPathFullyQualified(path) || !File.Exists(path)) return null;
            return new FileInfo(path);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static bool IsSuccess(string response)
    {
        try
        {
            using var document = JsonDocument.Parse(response);
            return document.RootElement.TryGetProperty("status", out var status)
                && string.Equals(status.GetString(), "success", StringComparison.Ordinal);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string Hash(string text)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static void TrimExpiredAndBounded(DateTimeOffset now)
    {
        foreach (var entry in Entries)
        {
            if (now - entry.Value.ReturnedAt > Ttl) Entries.TryRemove(entry.Key, out _);
        }

        if (Entries.Count <= MaxEntries) return;
        var oldest = Entries
            .OrderBy(entry => entry.Value.ReturnedAt)
            .Take(Entries.Count - MaxEntries)
            .Select(entry => entry.Key)
            .ToArray();
        foreach (var key in oldest) Entries.TryRemove(key, out _);
    }
}
