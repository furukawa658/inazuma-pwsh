# Zuma MCP reference

## Sessions and output

Choose a unique `sessionId` for each chat and reuse it within that chat. IDs contain 1 to 64 letters, digits, hyphens, or underscores. If omitted, calls share the `default` session. Calls with the same ID run sequentially; different IDs can run concurrently.

Variables, modules, and the current location are retained per runspace within the external worker process. A new or reset session starts in the first local VS Code workspace folder, passed as `ZUMA_WORKSPACE_ROOT`, or the user's home directory when no valid workspace root is available. Session state is ephemeral: resetting an existing session or an unexpected worker termination loses its state. Resetting a nonexistent session returns `existed: false` without creating it; resetting an existing session returns `existed: true`.

Each session has a separate PowerShell worker process. Changes to process environment variables (`$env:`) remain in that worker and its children; they do not update another session or the MCP server. Files, user/machine environment settings, and other operating system resources remain shared. This separation is not a security boundary.

There are at most six sessions, including `default`. Idle sessions expire after 10 minutes (the least recently used idle session is evicted at the limit) and are cleaned up approximately once a minute. Running, waiting, or stopping sessions are not removed; creating another session when all six are busy returns an error. `list_powershell_sessions` returns each session's ID, last known location, UTC creation and last-used times, and busy state. A running command's location is updated when the command finishes.

`execute_powershell` accepts an existing absolute `workingDirectory`. It sets the session's location before the command and remains in effect afterwards; `Set-Location` changes persist across calls as well. Its default timeout is 60 seconds; the build default is 120 seconds and test runner defaults are 180 seconds. Execution timeouts are clamped to 1 through 300 seconds.

`maxOutputChars` limits `output` to 1,000 through 50,000 characters, with a default of 6,000. When output is truncated, `data` is null and the full log is saved locally. This limit does not bound the full JSON response or memory used by a script. Return only the data you need.

`compactResponse` defaults to `true` and omits `output` when it exactly duplicates `data`. Set it to `false` to always include `output`. Truncated or differing output is retained. The effect on model tokens and credits has not been measured for complete chats; do not assert unmeasured token or cost savings.

Write-Host and information output appear in normal output in execution order. Warning messages, and verbose and debug messages prefixed with `VERBOSE:` or `DEBUG:`, appear in `warnings`. Terminating errors return `status: error` with the exception in `errors` and retain output produced before the error. A truncated execution log is plain UTF-8 text with output lines separated by newlines.

Python executables invoked without pipeline input receive closed standard input. Existing pipelines are preserved. Detection applies to command names that resolve to executables before execution, such as `python`, `python3`, and `py`; dynamic names and aliases changed during execution are not guaranteed. Interactive Python input is not supported.

Build and test runner diagnostic responses are bounded to 8,000 characters. Nonzero exit codes do not indicate success. Missing or invalid reports return `report_unavailable` or `error`. Inspect the full log for MSBuild failures outside compiler diagnostics. The MCP SDK may report argument type errors as tool errors rather than JSON-RPC `InvalidParams`.

Playwright is resolved from the target directory or its parents; dependencies are not installed with `npx`. The JSON reporter is managed by the tool. `inspect_playwright_trace` resolves a relative `tracePath` against the optional `workingDirectory`, or the server process current directory when omitted, and returns the absolute `tracePath`. Missing files return `status: error` with a message in `errors`. ANSI escapes are removed from returned text. Uncaught error stacks are arrays of trimmed frames with Playwright internal frames removed; an uncaught error identical to the failed action error is omitted. Trace inspection has limits on input, event count, DOM depth, and output. It does not replace visual testing of canvas, video, rendering, or CSS. Sensitive URL parameters are masked, but other secrets may remain.

The test runner tools manage their report options: `run_bun_test` rejects `--reporter*`, `run_pytest` rejects `--junitxml` and `--junit-xml`, and `run_vitest` rejects `--reporter` and `--outputFile`. Pass other arguments, such as test paths or filters, through `testArgs`. `run_pytest` uses `pythonPath` when given (an existing absolute path), otherwise the nearest `.venv\Scripts\python.exe` in the working directory or its parents, otherwise `python` on `PATH`; pytest exit code 5 is reported as `no_tests`. Vitest is resolved from the target directory or its parents and always runs once, not in watch mode. JUnit XML is read with DTD processing disabled. Reports larger than 50 MB return `report_unavailable`, and at most 10 failures are listed.

For the five test runners, `compactResponse: true` removes test progress, failure details and temporary report paths when a report is parsed successfully, retaining summaries and other output such as warnings or collection errors. Empty compact output is omitted. When a report is unavailable or execution is interrupted, output is not filtered. Set `compactResponse: false` to retain the raw output tail. The response size limit still applies; use `fullLogPath` for the complete log.

## MCP Tasks

The server supports the `io.modelcontextprotocol/tasks` extension with protocol revision `2026-07-28` or later. The client opts in on a request by adding the extension to `_meta/io.modelcontextprotocol/clientCapabilities/extensions` and setting `_meta/io.modelcontextprotocol/protocolVersion` to a task-capable revision.

Task execution is optional for `execute_powershell`, `run_dotnet_build`, `run_dotnet_test`, `run_affected_tests`, `run_playwright`, `run_bun_test`, `run_pytest`, `run_vitest`, `run_tsc`, `run_eslint`, and `run_cargo_check`. An opted-in call returns `resultType: "task"`; the client uses `tasks/get` to obtain `working`, `completed`, `failed`, or `cancelled` state. A completed task contains the same `CallToolResult` returned by a synchronous call. `tasks/cancel` signals cooperative cancellation and normally cancels the underlying process; the task state may briefly remain `working` until cleanup finishes.

Task records are kept in memory for one hour and are lost when the server process exits. The suggested polling interval is 250 ms. Calls to file-reading, log-summary, search, and trace tools remain synchronous. A client that uses an older protocol revision or does not opt in receives the existing synchronous result.

On Windows, the Rust front end uses a separate compatibility server process for task-capable 2026 requests. This keeps those requests independent from the existing 2024 initialized sidecar and its persistent sessions.

## Execution gate

`get_execution_gate`, `set_execution_gate`, and `reset_execution_gate` expose the 0.10.0 execution policy. The gate is disabled with `inazumaPwsh.executionGuard.enabled`; it is enabled by default.

The available modes are:

- `manual`: Preserve normal full-test behavior. The repeat-failure circuit breaker remains active.
- `incremental`: Block an unscoped full suite and direct the caller to `run_affected_tests` or a specific file/filter.
- `milestone`: Allow full regression suites.

The circuit breaker records a normalized command, a structured failure signature, and a Git diff fingerprint in the same workspace scope across typed diagnostic tools and PowerShell test commands. A circuit trips at `repeatFailureThreshold` identical failures, which defaults to 3 and is bounded from 2 through 10. `historySeconds` defaults to 120 and is bounded from 10 through 3600. The next execution is blocked until the command or Git diff changes, the history expires, the server restarts, or the gate is reset.

`reset_execution_gate` clears all history by default. A `workspaceKey` clears only one workspace scope. `set_execution_gate` can change mode, enablement, threshold, retention, and `skipRedPhase` without restarting the server.

When `skipRedPhase` is true, a test run is blocked if Git reports only test-file changes. The setting is false by default because reproduction tests, test infrastructure, and test-only corrections are legitimate full workflows.

VS Code exposes these settings under `inazumaPwsh.executionGuard.*`. The status bar shows the current mode or `Breaker tripped`; **Zuma MCP: Reset Execution Gate** restarts the server definition to obtain an empty process history. The server also writes minimal state to `%TEMP%/inazuma-pwsh-logs/execution-gate.json` for status display. This file contains mode and tripped workspace keys, not command output.

## File-reading tools

`peek_file` returns outline entries as strings in `start-end level title` form. `maxLevel` optionally filters out deeper entries before the `maxEntries` limit. Python definition ranges include preceding decorators; C# expression-bodied members are included.

`read_section` accepts a single `query` or `startLine` and optional `endLine`, or `ranges` containing up to 10 section-name strings or objects with `query` or `startLine` and optional `endLine`. The alternatives cannot be combined. Multiple results are returned in order as `ranges` items with `label` and `result`; errors for individual ranges appear in their result. `maxChars` applies to each range. An inverted line range returns an error. When a query has no outline match, `candidates` contains up to 10 similar titles, or `bodyMatches` contains up to 10 matching body lines if no title is close.

`read_symbol` accepts an exact, case-insensitive declaration name in a supported code file. It includes Python decorators and uses braces, indentation, or language delimiters to trim the result to the declaration rather than its containing scope. A unique match returns `title`, `startLine`, `endLine`, `symbolEndLine`, and numbered `text`. Multiple overloads or duplicate names return `status: "ambiguous"` with `matches`; pass the 1-based `occurrence` to select one. `maxChars` is clamped from 1,000 through 50,000 and defaults to 12,000; a truncated result identifies `nextLine`, which can be resumed with `read_section`. Missing names include up to 10 similar symbol candidates when available. Markdown, JSON, YAML, and plain text are rejected because they do not contain supported code declarations.

`search_files` accepts `literal`, `contextLines` (0 through 5), `maxPerFile` (1 through 100, default 20), and an `exclude` array of glob patterns relative to the search path. Results are distributed across matching files within `maxResults`; each file's `omitted` count appears when matches are not displayed. Optional `context` entries contain numbered `before` and `after` lines. Leading whitespace is retained. The search skips `.git`, `node_modules`, `.venv`, `bin`, `obj`, binary files and files over 5 MB. Inside a Git repository, Git's `check-ignore` applies ignore rules, including nested ignore files and tracked-file semantics; Git must be available on PATH.

`summarize_log` detects English and Japanese error and warning terms by default and ignores zero-count summaries for those terms. A custom `pattern` is used as supplied.

## Safety and logs

Scripts run with the current user's permissions. Zuma MCP is not a security sandbox, and syntax checks do not restrict unauthorized system operations. Connect only trusted local clients, and do not put credentials in scripts. Avoid interactive commands, GUI prompts, authentication prompts, and elevation requests.

Cancellation and timeouts request PowerShell to stop. An uninterruptible .NET call can continue after the response; its session remains unavailable while stopping. After a 10-second stop grace period, the server terminates the worker and its process tree (Windows Job Object on Windows, `Process.Kill(entireProcessTree)` on Linux/macOS); session state is lost. On Linux/macOS there is no Job Object, so workers may be orphaned if the server process crashes. Diagnostic tools also use a Windows Job Object on cancellation or timeout. The extension requests shutdown when its parent extension host exits.

Full logs are saved as UTF-8 under `%TEMP%/inazuma-pwsh-logs/`. `fullLogPath` identifies an execution log; `fullReportPath`, when present, identifies detailed structured diagnostics. Logs older than 3 days, or beyond 200 MB in total, are deleted at server startup, and they may contain sensitive output. Review and remove them when no longer needed.

## Build and verification

The compatibility source targets .NET 10 and ModelContextProtocol 2.4.0. Every platform package starts the Rust MCP entry point. Rust handles UTF-8 Markdown and code outlines, explicit line ranges, `search_files`, and `summarize_log` locally. It forwards persistent PowerShell, diagnostics, symbol reads, PowerShell AST parsing, non-UTF-8 fallback, and other tool paths over stdio to the self-contained, single-file C# core (`InazumaPwsh.core.exe` on Windows and `InazumaPwsh.core` elsewhere). Native AOT remains disabled for the C# core, while `powershell-worker.ps1` is deployed beside it. NuGet and Cargo audit direct and transitive dependencies during release verification; review the audit and latest runtime security updates before release.

The .NET 10 Native AOT evaluation can link the `win-x64` binary, but the build reports 50 `IL2026` and 50 `IL3050` warnings from reflection-based JSON serialization. More importantly, the resulting process fails before `initialize`: ModelContextProtocol and Microsoft.Extensions.AI cannot resolve source-generated `JsonTypeInfo` metadata for `JsonElement[]` while constructing the MCP tools. Do not enable AOT in the packaging workflow. Re-evaluate only after the MCP SDK supports this registration path under AOT or the application supplies a complete `JsonSerializerContext`, and require the full packaged MCP suite to initialize and pass after that migration.

From the project directory:

```powershell
dotnet build InazumaPwsh.csproj --nologo -v:q
cargo test --release --manifest-path rust/zuma-mcp/Cargo.toml
node vscode-extension/build-target.mjs win32-x64
node rust/zuma-mcp/tests/real-smoke.mjs vscode-extension/server/win-x64/InazumaPwsh.exe
# Build one VSIX on its matching OS/architecture: node vscode-extension/build-target.mjs <win32-x64|linux-x64|linux-arm64|darwin-x64|darwin-arm64>
```

Reproducibility requires the same source, SDK, runtime, dependencies, and build settings. Path mapping alone is not a guarantee across builds.

Prefer the default extraction cache. If you set `DOTNET_BUNDLE_EXTRACT_BASE_DIR`, use a short absolute Windows path with backslashes. In candidate tests, forward-slash values failed to start CoreCLR with `0x80070057`, and long extraction paths failed while writing bundled module files. The chosen cache must also be writable by the current user.

The tests use temporary projects and include mock Playwright and Vitest CLIs and a fake pytest interpreter. A mock test does not establish that a real browser workflow passed. The real `bun test` check is skipped when Bun is unavailable, and the real pytest check runs only when `INAZUMA_TEST_PYTHON` points to a Python executable with pytest installed. Python tests are skipped when Python is unavailable.
