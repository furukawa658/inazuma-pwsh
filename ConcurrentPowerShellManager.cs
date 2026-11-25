using System.Text.Json;
using Microsoft.Extensions.Hosting;

namespace InazumaPwsh;

public sealed class ConcurrentPowerShellManager : IDisposable
{
    private sealed class Entry
    {
        public PowerShellSession Session { get; } = new();
        public int Users { get; set; }
        public DateTime Created { get; } = DateTime.UtcNow;
        public DateTime LastUsed { get; set; } = DateTime.UtcNow;
    }

    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _sessions = new(StringComparer.Ordinal);
    private readonly CancellationToken _stopping;
    private readonly Timer _timer;
    private bool _disposed;

    // lock内でDisposeするため、Session.Dispose()がセマフォ待ちで長く止まらないことが前提
    public ConcurrentPowerShellManager(IHostApplicationLifetime lifetime)
    {
        _stopping = lifetime.ApplicationStopping;
        _timer = new Timer(_ => ReapIdleSessions(), null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
    }

    public Task<string> ExecuteAsync(string script, string? workingDirectory, int timeoutSeconds, int maxOutputChars,
        string? sessionId, bool compactResponse = true, CancellationToken cancellationToken = default) => UseAsync(sessionId, async session =>
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stopping);
        return await session.ExecuteScriptAsync(script, timeoutSeconds, linked.Token, workingDirectory, maxOutputChars, compactResponse);
    });

    public Task<string> ResetAsync(string? sessionId, CancellationToken cancellationToken) => UseAsync(sessionId, async session =>
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stopping);
        return await session.ResetSessionAsync(linked.Token);
    }, createIfMissing: false);

    public string ListSessions()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return JsonDefaults.Serialize(new
            {
                status = "success",
                note = _sessions.Count == 0
                    ? "No active sessions. Sessions are created by a successful execute and removed after 10 idle minutes; calls blocked by the execution gate do not create one."
                    : null,
                sessions = _sessions.Select(pair => new
                {
                    sessionId = pair.Key,
                    currentLocation = pair.Value.Session.CurrentLocation,
                    createdAt = pair.Value.Created,
                    lastUsedAt = pair.Value.LastUsed,
                    busy = pair.Value.Users > 0 || pair.Value.Session.IsStopping
                }).ToArray()
            });
        }
    }

    private async Task<string> UseAsync(string? sessionId, Func<PowerShellSession, Task<string>> action, bool createIfMissing = true)
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
                if (!createIfMissing)
                    return JsonDefaults.Serialize(new { status = "success", existed = false, data = "Session does not exist." });
                if (_sessions.Count >= 6)
                {
                    var victim = _sessions.Where(pair => pair.Value.Users == 0 && !pair.Value.Session.IsStopping)
                        .OrderBy(pair => pair.Value.LastUsed).Select(pair => (KeyValuePair<string, Entry>?)pair).FirstOrDefault();
                    if (victim == null) return Error("Session limit reached (6 including default) and all sessions are busy. Reuse an existing sessionId.");
                    _sessions.Remove(victim.Value.Key);
                    victim.Value.Value.Session.Dispose();
                }
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
            foreach (var pair in _sessions.Where(pair => pair.Value.Users == 0 && !pair.Value.Session.IsStopping
                && DateTime.UtcNow - pair.Value.LastUsed > TimeSpan.FromMinutes(10)).ToArray())
            {
                _sessions.Remove(pair.Key);
                pair.Value.Session.Dispose();
            }
        }
    }

    private static string Error(string message) => JsonDefaults.Serialize(new
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