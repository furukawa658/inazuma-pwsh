using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using InazumaPwsh;
using System.Diagnostics;

Process? parent = null;
var parentArgument = Array.IndexOf(args, "--parent-pid");
if (parentArgument >= 0)
{
    if (parentArgument + 1 >= args.Length || !int.TryParse(args[parentArgument + 1], out var parentId) || parentId <= 0)
    {
        Console.Error.WriteLine("--parent-pid requires a positive process ID.");
        Environment.ExitCode = 2;
        return;
    }
    try { parent = Process.GetProcessById(parentId); }
    catch (ArgumentException) { return; }
}
using var parentProcess = parent;

// アプリケーションホストビルダーの作成
var builder = Host.CreateApplicationBuilder(args);

// ログは必ずstderrへ設定し、stdoutにJSON-RPC以外の文字を絶対に出さない
builder.Logging.ClearProviders();
builder.Logging.AddConsole(options =>
{
    options.LogToStandardErrorThreshold = LogLevel.Trace;
});

// 常駐型PowerShellセッションをシングルトン登録
builder.Services.AddSingleton<ConcurrentPowerShellManager>();

// MCPサーバーの設定: stdioトランスポートとPowerShellTools
builder.Services.AddMcpServer()
    .WithStdioServerTransport()
    .WithTools<PowerShellTools>()
    .WithTools<DiagnosticTools>();

using var host = builder.Build();
var lifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();
var parentWatcher = parent == null ? Task.CompletedTask : WatchParentAsync(parent, lifetime);

// サーバー起動時にRunspaceを明示的に初期化して常駐
var session = host.Services.GetRequiredService<ConcurrentPowerShellManager>();
session.Initialize();

try { await host.RunAsync(); }
catch (OperationCanceledException) when (lifetime.ApplicationStopping.IsCancellationRequested) { }
await parentWatcher;

static async Task WatchParentAsync(Process parent, IHostApplicationLifetime lifetime)
{
    try
    {
        await parent.WaitForExitAsync(lifetime.ApplicationStopped);
        lifetime.StopApplication();
    }
    catch (OperationCanceledException) { }
    catch (InvalidOperationException) { lifetime.StopApplication(); }
}
