namespace InazumaPwsh;

internal static class LogCleaner
{
    private static readonly TimeSpan MaxAge = TimeSpan.FromDays(3);
    private const long MaxTotalBytes = 200L * 1024 * 1024;

    public static void StartBackgroundCleanup() => _ = Task.Run(Clean);

    internal static void Clean()
    {
        try
        {
            var directory = Path.Combine(Path.GetTempPath(), "inazuma-pwsh-logs");
            if (!Directory.Exists(directory)) return;
            var files = new DirectoryInfo(directory).EnumerateFiles().OrderBy(file => file.LastWriteTimeUtc).ToList();
            var total = files.Sum(file => file.Length);
            var cutoff = DateTime.UtcNow - MaxAge;
            foreach (var file in files)
            {
                if (file.LastWriteTimeUtc >= cutoff && total <= MaxTotalBytes) break;
                try { file.Delete(); total -= file.Length; } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Log cleanup skipped: {ex.Message}");
        }
    }
}
