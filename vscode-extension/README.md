<p align="center"><img src="media/icon.png" alt="Zuma MCP icon" width="128"></p>

# Zuma MCP

**Your coding agent reads 7% of a design document and 1% of a build log, not 100%.**

![Before/After: full read vs Zuma MCP. 68,000 chars -> 7%, 137,000 chars -> 1%](media/before-after.gif)

| Input | Full read | With Zuma MCP | Characters returned |
| --- | --- | --- | --- |
| 68,000-character Markdown design document | 68,000 | `peek_file` | **7%** (about 4,760) |
| same document | 68,000 | `peek_file` + one `read_section` | **20%** (about 13,600) |
| 137,000-character execution log | 137,000 | `summarize_log` | **1%** (about 1,370) |

These are single-file measurements. Savings across complete chats have not been measured.

Zuma MCP (formerly InazumaPwsh) connects your VS Code coding agent to persistent local PowerShell 7 sessions. Reuse variables, modules, and your working location across tool calls; investigate .NET build, TypeScript (`tsc`), Playwright, bun test, pytest, and Vitest failures through structured diagnostics; and read large files and logs by outline, section, or summary, with full logs available when you need the details.

## What's new in 0.5.0

- **`run_tsc`.** Runs the locally installed TypeScript compiler without emitting files and returns diagnostics with file, line, column, code, and message, using the same bounded responses as the other diagnostic tools.
- **`contextLines` for the diagnostic tools.** Shows 0 to 10 source lines around each failure location as `codeContext` (default 0 = off). `summarize_log` also takes `contextLines` (0 to 5, default 1) and drops context first when the response is trimmed.
- **Linux and macOS (experimental).** The server now builds for the host platform. The project is tested locally only on Windows (41 of 41 checks pass); a Linux/macOS/Windows CI matrix is added but has not run yet. Linux and macOS have no Job Object, so a crashed server can leave worker processes behind. The pwsh override is still `INAZUMA_PWSH_PATH` or the `inazumaPwsh.pwshPath` setting.
- **Rename.** The display name is now Zuma MCP; the extension ID stays `ShunsukeF.inazuma-pwsh`. Setting keys, environment variables, executable names, and the log folder keep their `inazuma` names for compatibility.
## What's new in 0.4.1

0.4.1 shortens responses and reduces idle resource use.

- **Complex objects return text.** Output such as `Get-ChildItem` or `Get-Item` (FileInfo, Process and other .NET objects) is returned as formatted text instead of a depth-5 JSON tree that could reach tens of megabytes. Strings, numbers, hashtables, `[pscustomobject]` and arrays of them still return structured `data`; JSON depth is now 3. Use `ConvertTo-Json` in the script when you need full structure.
- **Shorter responses.** Empty `errors`/`warnings`, `isTruncated: false`, `exitCode: 0`, null `data`/`output`/`fullLogPath` and `stats` are omitted. `stats`, `isTruncated` and `fullLogPath` appear only when output is truncated. This is a behavior change for clients that read these fields unconditionally.
- **Lazy default session.** The server no longer starts a `default` PowerShell worker at launch; it starts on first use. The pwsh path and worker script are still validated at startup.
- **Session reclaim.** Idle sessions (including `default`) expire after 10 minutes. At the six-session limit, the least recently used idle session is evicted instead of failing.
- **Log cleanup.** At startup, files in `%TEMP%/inazuma-pwsh-logs/` older than 3 days are deleted, and the oldest are deleted while the total exceeds 200 MB.
## What's new in 0.4.0

0.4.0 helps your agent read less to find what it needs, and removes wasted characters from every response.

### New file-reading tools

Instead of loading a whole file into the conversation, your agent can look at its structure first and then read only the part it needs.

- **`peek_file`** returns the file type, encoding, line count, and an outline with line ranges: Markdown headings (code fences ignored), C#/TypeScript/JavaScript/PowerShell/Python/Go/Rust/VB declarations, JSON keys, and YAML keys. It also recommends `FULL` for short files (200 lines or fewer by default) or `PARTIAL` for longer ones.
- **`read_section`** returns one outline entry found by a title search (`query`), or an explicit `startLine`/`endLine` range, with line numbers. Long ranges stop at `maxChars` and give the `nextLine` to continue from.
- **`summarize_log`** groups lines that match a pattern (by default `error|fail|exception|fatal|warn`) after replacing digits, so repeated errors appear once with a count, first and last line, and context. It also returns the last lines of the log. Use it on build logs or on a `fullLogPath`.

All three require an existing absolute path, accept files up to 50 MB, read UTF-8 (with or without BOM) and UTF-16 with BOM, and fall back to Shift_JIS for non-UTF-8 text. `summarize_log` also strips ANSI color codes.

Measured examples: on a 68,000-character Markdown design document, `peek_file` returned 7% of the original characters, and `peek_file` plus one `read_section` call returned 20%. On a 137,000-character execution log, `summarize_log` returned 1%. These are single-file measurements; savings across complete chats have not been measured.

### Smaller responses

- **No unnecessary escaping.** Responses previously escaped quotes, `<`, `>`, `&`, `+`, and every non-ASCII character as `\uXXXX`, so each Japanese character took six characters. Responses now keep these characters as-is; quotes inside strings use the shorter `\"`.
- **`compactResponse` now defaults to `true`.** `output` is omitted when it exactly duplicates `data`. Pass `compactResponse: false` to always receive `output`. This is a behavior change.
- **Redirected stderr stays plain text.** Native command stderr merged with `2>&1` was returned as a fully serialized error object, often thousands of characters per line. It is now returned as the line's text.

### Fixes

- **Japanese output from native commands.** Workers now set the console input and output encoding to UTF-8, so output from commands such as `git log` is no longer garbled.

### Earlier: 0.3.0

`run_bun_test`, `run_pytest`, and `run_vitest` joined `run_dotnet_build` and `run_playwright`. They return test counts and failures with file, line, and message, using the status values `success`, `error`, `no_tests`, or `report_unavailable`. A run without a usable report is never reported as `success`. Responses are limited to 8,000 characters and list at most 10 failures; the full log is saved for follow-up searches.

## Why use Zuma MCP?

- **Set up once, continue working.** Keep a runspace ready for the next command instead of repeating imports and recreating variables on every call.
- **Give each chat its own working state.** Separate session IDs keep variables, modules, locations, and worker process environments independent. Different sessions can execute concurrently; files and other operating system resources remain shared.
- **Keep long output out of the conversation.** Bounded responses and optional duplicate-output removal keep routine results concise. Full logs remain available for follow-up searches. Token and credit savings have not been measured across complete chats.
- **Start troubleshooting with evidence.** Retrieve compiler diagnostics, test failure summaries, attachment paths, and trace findings without manually copying entire terminal logs into chat.
- **Automatic MCP integration.** The extension registers its MCP server automatically in VS Code. It bundles the server binary and worker script (`powershell-worker.ps1`), launching external PowerShell 7 workers on demand. Project-specific SDKs and test dependencies remain required.

## Try it in a chat

After installing the extension and approving its MCP tools, ask your agent to use InazumaPwsh. For example:

- "Use a dedicated Zuma MCP session for this chat. Load my module, then reuse it for the next commands."
- "Build this .NET project with InazumaPwsh. Show the compiler errors first and consult the full log if the build fails outside compilation."
- "Run this project's Playwright tests and summarize the failures and available trace paths."
- "Inspect this Playwright trace for failed actions, HTTP errors, and console errors."
- "Run the pytest suite with Zuma MCP and fix the first failure."
- "Run `bun test` with Zuma MCP and show me the failing files and lines."
- "Run Vitest with Zuma MCP for `src/utils` only and explain why the import failed."
- "Peek at this design document with Zuma MCP, then read only the section about approvals."
- "Summarize the errors in this build log with Zuma MCP before opening it."

The agent must choose and reuse a unique `sessionId` to keep chat state separate. Trace inspection complements browser-based visual checks; it does not verify how a page looks.

## Requirements and installation

- VS Code 1.101 or later. Windows x64 is the verified platform; Linux (x64, arm64) and macOS (x64, arm64) are supported but experimental (see "What's new in 0.5.0").
- External PowerShell 7 (`pwsh.exe` on Windows, `pwsh` on Linux/macOS) installed on the system (only PowerShell 7.7.0 has been verified in practice). The server searches `PATH` by default, or you can specify an explicit executable path via the `inazumaPwsh.pwshPath` setting or `INAZUMA_PWSH_PATH` environment variable. An invalid explicit path fails immediately without fallback.
- The extension bundles the server binary and the worker script (`powershell-worker.ps1`). The worker script runs under the external PowerShell 7 process to manage session runspaces.
- A trusted workspace. The extension runs in the local extension host, including when a remote workspace is open; it does not switch to the remote shell or filesystem.
- Install the VSIX for your platform (win32-x64, linux-x64, linux-arm64, darwin-x64 or darwin-arm64) using **Extensions: Install from VSIX** or from the Visual Studio Code Marketplace. The extension registers its MCP server automatically; no `mcp.json` entry is required. Follow VS Code's prompts to trust the workspace, start the server, and approve tool calls.
- Build diagnostics require the .NET SDK; Playwright diagnostics require Node.js and a locally installed Playwright package. `run_bun_test` requires Bun on `PATH`, `run_pytest` requires a Python interpreter with pytest installed, and `run_vitest` requires Node.js and a locally installed Vitest package.

The Zuma MCP project code is licensed under the MIT License, copyright 2026 Shunsuke F. Bundled third-party components retain their own licenses; see `LICENSE` and `THIRD-PARTY-NOTICES.txt` for details. Repository: https://github.com/furukawa658/inazuma-pwsh

## Tools

| Tool | Purpose |
| --- | --- |
| `execute_powershell` | Run a script in a persistent session and return structured data, output, errors, exit code, and a full-log path when truncated. |
| `reset_powershell_session` | Recreate one worker and its session environment. It does not undo changes to files or user/machine environment settings. |
| `run_dotnet_build` | Run a build and return the exit code and SARIF compiler diagnostics. Warnings are omitted by default. |
| `run_playwright` | Run locally installed Playwright and return test counts, failures, and attachment paths from its JSON report. |
| `inspect_playwright_trace` | Inspect a trace ZIP for failed actions, HTTP and console errors, and a DOM excerpt. |
| `run_bun_test` | Run `bun test` and return test counts and failures with file, line, message, and stack from its JUnit XML report. |
| `run_pytest` | Run pytest and return test counts and failures with file, line, and message from its JUnit XML report. |
| `run_vitest` | Run locally installed Vitest once (`vitest run`) and return test counts, failures, and file-level errors from its JSON report. |
| `run_tsc` | Run the locally installed TypeScript compiler (`tsc --noEmit`) and return diagnostics with file, line, column, code, and message. |
| `peek_file` | Return a text file's type, encoding, line count, outline with line ranges, and a `FULL` or `PARTIAL` reading recommendation. |
| `read_section` | Return one outline entry by title search, or a line range, with line numbers and a `nextLine` for continuation. |
| `summarize_log` | Group matching log lines with counts and context, and return the last lines of the log. |

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
