# Zuma MCP reference

## Sessions and output

Choose a unique `sessionId` for each chat and reuse it within that chat. IDs contain 1 to 64 letters, digits, hyphens, or underscores. If omitted, calls share the `default` session. Calls with the same ID run sequentially; different IDs can run concurrently.

Variables, modules, and the current location are retained per runspace within the external worker process. However, session state is ephemeral: calling `reset_powershell_session` or encountering an unexpected worker process termination causes complete state loss, recreating the session from a clean initial state.

Each session has a separate PowerShell worker process. Changes to process environment variables (`$env:`) remain in that worker and its children; they do not update another session or the MCP server. Files, user/machine environment settings, and other operating system resources remain shared. This separation is not a security boundary.

There are at most six sessions, including `default`. Idle sessions expire after 10 minutes (the least recently used idle session is evicted at the limit) and are cleaned up approximately once a minute. Running or waiting sessions are not removed.

`execute_powershell` accepts an existing absolute `workingDirectory` for one call. Its default timeout is 60 seconds; the build default is 120 seconds and test runner defaults are 180 seconds. Execution timeouts are clamped to 1 through 300 seconds.

`maxOutputChars` limits `output` to 1,000 through 50,000 characters, with a default of 6,000. When output is truncated, `data` is null and the full log is saved locally. This limit does not bound the full JSON response or memory used by a script. Return only the data you need.

`compactResponse` defaults to `true` and omits `output` when it exactly duplicates `data`. Set it to `false` to always include `output`. Truncated or differing output is retained. The effect on model tokens and credits has not been measured for complete chats; do not assert unmeasured token or cost savings.

Python executables invoked without pipeline input receive closed standard input. Existing pipelines are preserved. Detection applies to command names that resolve to executables before execution, such as `python`, `python3`, and `py`; dynamic names and aliases changed during execution are not guaranteed. Interactive Python input is not supported.

Build and test runner diagnostic responses are bounded to 8,000 characters. Nonzero exit codes do not indicate success. Missing or invalid reports return `report_unavailable` or `error`. Inspect the full log for MSBuild failures outside compiler diagnostics. The MCP SDK may report argument type errors as tool errors rather than JSON-RPC `InvalidParams`.

Playwright is resolved from the target directory or its parents; dependencies are not installed with `npx`. The JSON reporter is managed by the tool. Trace inspection has limits on input, event count, DOM depth, and output. It does not replace visual testing of canvas, video, rendering, or CSS. Sensitive URL parameters are masked, but other secrets may remain.

The test runner tools manage their report options: `run_bun_test` rejects `--reporter*`, `run_pytest` rejects `--junitxml` and `--junit-xml`, and `run_vitest` rejects `--reporter` and `--outputFile`. Pass other arguments, such as test paths or filters, through `testArgs`. `run_pytest` uses `pythonPath` when given (an existing absolute path), otherwise the nearest `.venv\Scripts\python.exe` in the working directory or its parents, otherwise `python` on `PATH`; pytest exit code 5 is reported as `no_tests`. Vitest is resolved from the target directory or its parents and always runs once, not in watch mode. JUnit XML is read with DTD processing disabled. Reports larger than 50 MB return `report_unavailable`, and at most 10 failures are listed.

## Safety and logs

Scripts run with the current user's permissions. Zuma MCP is not a security sandbox, and syntax checks do not restrict unauthorized system operations. Connect only trusted local clients, and do not put credentials in scripts. Avoid interactive commands, GUI prompts, authentication prompts, and elevation requests.

Cancellation and timeouts request PowerShell to stop. An uninterruptible .NET call can continue after the response; its session remains unavailable while stopping. After a 10-second stop grace period, the server terminates the worker and its process tree (Windows Job Object on Windows, `Process.Kill(entireProcessTree)` on Linux/macOS); session state is lost. On Linux/macOS there is no Job Object, so workers may be orphaned if the server process crashes. Diagnostic tools also use a Windows Job Object on cancellation or timeout. The extension requests shutdown when its parent extension host exits.

Full logs are saved as UTF-8 under `%TEMP%/inazuma-pwsh-logs/`. `fullLogPath` identifies an execution log; `fullReportPath`, when present, identifies detailed structured diagnostics. Logs older than 3 days, or beyond 200 MB in total, are deleted at server startup, and they may contain sensitive output. Review and remove them when no longer needed.

## Build and verification

The source targets .NET 10 and ModelContextProtocol 2.4.0. Each platform build is self-contained and single-file; on first launch it extracts assemblies to a cache. Trimming and Native AOT are disabled. The worker script `powershell-worker.ps1` is deployed alongside the binary. NuGet audits direct and transitive dependencies during restore; review the audit and the latest runtime security updates before release.

From the project directory:

```powershell
dotnet build InazumaPwsh.csproj --nologo -v:q
node test-mcp.mjs
$sourceRoot = (Get-Location).Path
dotnet publish InazumaPwsh.csproj -c Release -p:RuntimeFrameworkVersion=10.0.15 "-p:PathMap=$sourceRoot=/_/inazuma-pwsh" -o artifacts/win-x64-0.5.0 --nologo -v:q
node test-mcp.mjs artifacts/win-x64-0.5.0/InazumaPwsh.exe
# Build one VSIX per platform (cross-publishes the server): node vscode-extension/build-target.mjs <win32-x64|linux-x64|linux-arm64|darwin-x64|darwin-arm64>
```

Reproducibility requires the same source, SDK, runtime, dependencies, and build settings. Path mapping alone is not a guarantee across builds.

Prefer the default extraction cache. If you set `DOTNET_BUNDLE_EXTRACT_BASE_DIR`, use a short absolute Windows path with backslashes. In candidate tests, forward-slash values failed to start CoreCLR with `0x80070057`, and long extraction paths failed while writing bundled module files. The chosen cache must also be writable by the current user.

The tests use temporary projects and include mock Playwright and Vitest CLIs and a fake pytest interpreter. A mock test does not establish that a real browser workflow passed. The real `bun test` check is skipped when Bun is unavailable, and the real pytest check runs only when `INAZUMA_TEST_PYTHON` points to a Python executable with pytest installed. Python tests are skipped when Python is unavailable.
