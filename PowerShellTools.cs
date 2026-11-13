using System.ComponentModel;
using ModelContextProtocol.Server;

namespace InazumaPwsh;

/// <summary>
/// 常駐型PowerShellセッションを操作するMCPツール群。
/// </summary>
[McpServerToolType]
public sealed class PowerShellTools
{
    private readonly ConcurrentPowerShellManager _session;

    public PowerShellTools(ConcurrentPowerShellManager session)
    {
        _session = session;
    }

    [McpServerTool(Name = "execute_powershell")]
    [Description("Run PowerShell in a persistent session. Calls with the same sessionId run sequentially and retain variables; different IDs can run in parallel. Use a unique sessionId for each chat. Environment variables are shared by the process; avoid changing them. Long output is truncated; search fullLogPath with [System.IO.File]::ReadLines. Avoid interactive commands.")]
    public async Task<string> ExecutePowerShellAsync(
        [Description("PowerShell script to execute. For efficient log searches, use .NET APIs such as [System.IO.File]::ReadLines instead of Get-Content | Select-String.")]
        string script,
        [Description("Timeout in seconds (default: 60).")]
        int timeoutSeconds = 60,
        [Description("Absolute working directory for this call. If omitted, the session's current location is used.")]
        string? workingDirectory = null,
        [Description("Maximum output text length in characters: 1000 to 50000, default 6000. The full log is saved to fullLogPath when truncated.")]
        int maxOutputChars = 6000,
        [Description("A unique, stable ID for each chat, up to 64 letters, digits, hyphens or underscores. If omitted, the shared default session is used.")]
        string? sessionId = null,
        [Description("When true, omit output if it exactly duplicates data. Truncated or differing output is retained.")]
        bool compactResponse = false,
        CancellationToken cancellationToken = default)
    {
        return await _session.ExecuteAsync(script, workingDirectory, Math.Clamp(timeoutSeconds, 1, 300),
            Math.Clamp(maxOutputChars, 1000, 50000), sessionId, compactResponse, cancellationToken);
    }

    [McpServerTool(Name = "reset_powershell_session")]
    [Description("Dispose and recreate a persistent PowerShell session (runspace), resetting its state.")]
    public async Task<string> ResetPowerShellSessionAsync(
        [Description("Session ID to reset. If omitted, only the default session is reset. Environment variables are not reset.")]
        string? sessionId = null, CancellationToken cancellationToken = default)
    {
        return await _session.ResetAsync(sessionId, cancellationToken);
    }
}
