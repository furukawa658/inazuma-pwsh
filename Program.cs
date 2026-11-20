using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Extensions.Tasks;
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

var taskStore = new InMemoryMcpTaskStore
{
    DefaultPollIntervalMs = 250,
    DefaultTimeToLive = TimeSpan.FromHours(1)
};
var asynchronousTools = new HashSet<string>(StringComparer.Ordinal)
{
    "execute_powershell",
    "run_dotnet_build",
    "run_dotnet_test",
    "run_affected_tests",
    "run_playwright",
    "run_bun_test",
    "run_pytest",
    "run_vitest",
    "run_tsc",
    "run_eslint",
    "run_cargo_check"
};

// MCPサーバーの設定: stdioトランスポートとPowerShellTools
builder.Services.AddMcpServer()
    .WithStdioServerTransport()
    .WithTools<PowerShellTools>()
    .WithTools<DiagnosticTools>()
    .WithTools<FileReadingTools>()
    .WithTasks(taskStore, options =>
    {
        options.ExecutionModeSelector = request =>
            asynchronousTools.Contains(request.Params?.Name ?? string.Empty)
                ? McpTaskExecutionMode.Optional
                : McpTaskExecutionMode.Synchronous;
    });

using var host = builder.Build();
var lifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();
var parentWatcher = parent == null ? Task.CompletedTask : WatchParentAsync(parent, lifetime);

// pwsh/ワーカーの解決だけを起動時に検証し、ワーカーは最初の呼出しで起動する
try { PowerShellSession.ValidateEnvironment(); }
catch (FileNotFoundException error)
{
    Console.Error.WriteLine($"InazumaPwsh cannot start: {error.Message}");
    Environment.ExitCode = 1;
    return;
}
LogCleaner.StartBackgroundCleanup();

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
