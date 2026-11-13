using System.Text.Json;
using Microsoft.Extensions.Hosting;

namespace InazumaPwsh;

public sealed class ConcurrentPowerShellManager : IDisposable
{
    private sealed class Entry
    {
        public PowerShellSession Session { get; } = new();
        public int Users { get; set; }
        public DateTime LastUsed { get; set; } = DateTime.UtcNow;
    }

    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _sessions = new(StringComparer.Ordinal);
    private readonly CancellationToken _stopping;
    private readonly Timer _timer;
    private bool _disposed;

    public ConcurrentPowerShellManager(IHostApplicationLifetime lifetime)
    {
        _stopping = lifetime.ApplicationStopping;
        _timer = new Timer(_ => ReapIdleSessions(), null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
    }

    public void Initialize() => UseAsync(null, session =>
    {
        session.Initialize();
        return Task.FromResult("");
    }).GetAwaiter().GetResult();

    public Task<string> ExecuteAsync(string script, string? workingDirectory, int timeoutSeconds, int maxOutputChars,
        string? sessionId, bool compactResponse, CancellationToken cancellationToken) => UseAsync(sessionId, async session =>
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stopping);
        return await session.ExecuteScriptAsync(script, timeoutSeconds, linked.Token, workingDirectory, maxOutputChars, compactResponse);
    });

    public Task<string> ResetAsync(string? sessionId, CancellationToken cancellationToken) => UseAsync(sessionId, async session =>
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stopping);
        return await session.ResetSessionAsync(linked.Token);
    });

    private async Task<string> UseAsync(string? sessionId, Func<PowerShellSession, Task<string>> action)
    {
        var key = sessionId ?? "default";
        if (key.Length is < 1 or > 64 || !key.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_'))
            return Error("sessionId must contain 1-64 ASCII letters, digits, hyphens or underscores.");

        Entry entry;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_sessions.TryGetValue(key, out entry!))
            {
                if (_sessions.Count >= 6) return Error("Session limit reached (6 including default). Reuse an existing sessionId.");
                entry = new Entry();
                _sessions.Add(key, entry);
            }
            entry.Users++;
        }
        try
        {
            return await action(entry.Session);
        }
        finally
        {
            lock (_gate)
            {
                entry.Users--;
                entry.LastUsed = DateTime.UtcNow;
            }
        }
    }

    private void ReapIdleSessions()
    {
        lock (_gate)
        {
            if (_disposed) return;
            foreach (var pair in _sessions.Where(pair => pair.Key != "default" && pair.Value.Users == 0 && !pair.Value.Session.IsStopping
                && DateTime.UtcNow - pair.Value.LastUsed > TimeSpan.FromMinutes(30)).ToArray())
            {
                _sessions.Remove(pair.Key);
                pair.Value.Session.Dispose();
            }
        }
    }

    private static string Error(string message) => JsonSerializer.Serialize(new
    {
        status = "error", data = (object?)null, errors = new[] { message }, warnings = Array.Empty<string>(), durationMs = 0
    });

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _timer.Dispose();
            foreach (var entry in _sessions.Values) entry.Session.Dispose();
            _sessions.Clear();
        }
    }
}