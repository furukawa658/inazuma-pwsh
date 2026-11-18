namespace InazumaPwsh;

/// <summary>Appends the size of what a tool read and what it returned, so that savings can be measured.</summary>
internal static class UsageLog
{
    private const long MaxBytes = 5L * 1024 * 1024;
    private static readonly object Gate = new();

    public static void Record(string tool, long sourceBytes, string response)
    {
        if (sourceBytes <= 0)
        {
            RecordCall(tool);
            return;
        }
        Append(new { time = DateTime.UtcNow.ToString("o"), tool, sourceBytes, responseBytes = System.Text.Encoding.UTF8.GetByteCount(response) });
    }

    public static void RecordCall(string tool) => Append(new { time = DateTime.UtcNow.ToString("o"), tool, countOnly = true });

    private static void Append(object entry)
    {
        try
        {
            lock (Gate)
            {
                var directory = Path.Combine(Path.GetTempPath(), "inazuma-pwsh-logs");
                Directory.CreateDirectory(directory);
                var file = new FileInfo(Path.Combine(directory, "usage.jsonl"));
                if (file.Exists && file.Length > MaxBytes) file.Delete();
                File.AppendAllText(file.FullName, JsonDefaults.Serialize(entry) + "\n");
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
    }

    public static async Task<string> TrackCallAsync(string tool, Task<string> response)
    {
        var json = await response;
        RecordCall(tool);
        return json;
    }

    public static async Task<string> TrackFileAsync(string tool, string path, Task<string> response)
    {
        var json = await response;
        try { Record(tool, new FileInfo(path).Length, json); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { RecordCall(tool); }
        return json;
    }
}
