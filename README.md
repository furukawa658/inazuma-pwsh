<p align="center"><img src="vscode-extension/media/icon.png" alt="InazumaPwsh icon" width="128"></p>

# InazumaPwsh

**Keep your PowerShell setup between calls. Get the results your coding agent needs.**

InazumaPwsh connects your VS Code coding agent to persistent local PowerShell sessions on Windows x64. Reuse variables, loaded modules, and your working location across tool calls. Investigate .NET build errors and Playwright, bun test, pytest, and Vitest failures through structured diagnostics, with full logs available when you need the details.

It is built for Windows automation, .NET development, and projects that use Playwright, bun test, pytest, or Vitest.

## What's new in 0.3.0

Three test runner tools join `run_dotnet_build` and `run_playwright`. Instead of reading raw terminal output, your agent receives test counts and a list of failures with file, line, and message.

- **`run_bun_test`** runs `bun test` and reads its JUnit XML report, including stack traces.
- **`run_pytest`** runs pytest and reads its JUnit XML report. It finds the nearest `.venv` automatically, or you can pass `pythonPath`.
- **`run_vitest`** runs locally installed Vitest once (`vitest run`) and reads its JSON report, including file-level errors such as import failures.

All three return the same status values: `success`, `error` (failed tests or a failed run), `no_tests`, or `report_unavailable`. A run without a usable report is never reported as `success`. Responses are limited to 8,000 characters and list at most 10 failures; the full log is saved for follow-up searches.

## Why use InazumaPwsh?

- **Set up once, continue working.** Keep a runspace ready for the next command instead of repeating imports and recreating variables on every call.
- **Give each chat its own working state.** Separate session IDs keep variables, modules, locations, and worker process environments independent. Different sessions can execute concurrently; files and other operating system resources remain shared.
- **Keep long output out of the conversation.** Bounded responses and optional duplicate-output removal keep routine results concise. Full logs remain available for follow-up searches. Token and credit savings have not been measured across complete chats.
- **Start troubleshooting with evidence.** Retrieve compiler diagnostics, test failure summaries, attachment paths, and trace findings without manually copying entire terminal logs into chat.
- **Automatic MCP integration.** The extension registers its MCP server automatically in VS Code. It bundles the server binary and worker script (`powershell-worker.ps1`), launching external PowerShell 7 workers on demand. Project-specific SDKs and test dependencies remain required.

## Try it in a chat

After installing the extension and approving its MCP tools, ask your agent to use InazumaPwsh. For example:

- "Use a dedicated InazumaPwsh session for this chat. Load my module, then reuse it for the next commands."
- "Build this .NET project with InazumaPwsh. Show the compiler errors first and consult the full log if the build fails outside compilation."
- "Run this project's Playwright tests and summarize the failures and available trace paths."
- "Inspect this Playwright trace for failed actions, HTTP errors, and console errors."
- "Run the pytest suite with InazumaPwsh and fix the first failure."
- "Run `bun test` with InazumaPwsh and show me the failing files and lines."
- "Run Vitest with InazumaPwsh for `src/utils` only and explain why the import failed."

The agent must choose and reuse a unique `sessionId` to keep chat state separate. Trace inspection complements browser-based visual checks; it does not verify how a page looks.

## Requirements and installation

- Windows x64 and VS Code 1.101 or later.
- External PowerShell 7 (`pwsh.exe`) installed on the system (only PowerShell 7.7.0 has been verified in practice). The server searches `PATH` by default, or you can specify an explicit executable path via the `inazumaPwsh.pwshPath` setting or `INAZUMA_PWSH_PATH` environment variable. An invalid explicit path fails immediately without fallback.
- The extension bundles the server binary and the worker script (`powershell-worker.ps1`). The worker script runs under the external PowerShell 7 process to manage session runspaces.
- A trusted workspace. The extension runs in the local extension host, including when a remote workspace is open; it does not switch to the remote shell or filesystem.
- Install the supplied Windows x64 VSIX using **Extensions: Install from VSIX** or from the Visual Studio Code Marketplace. The extension registers its MCP server automatically; no `mcp.json` entry is required. Follow VS Code's prompts to trust the workspace, start the server, and approve tool calls.
- Build diagnostics require the .NET SDK; Playwright diagnostics require Node.js and a locally installed Playwright package. `run_bun_test` requires Bun on `PATH`, `run_pytest` requires a Python interpreter with pytest installed, and `run_vitest` requires Node.js and a locally installed Vitest package.

The InazumaPwsh project code is licensed under the MIT License, copyright 2026 Shunsuke F. Bundled third-party components retain their own licenses; see `LICENSE` and `THIRD-PARTY-NOTICES.txt` for details. Repository: https://github.com/furukawa658/inazuma-pwsh

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

## Sessions and output

Choose a unique `sessionId` for each chat and reuse it within that chat. IDs contain 1 to 64 letters, digits, hyphens, or underscores. If omitted, calls share the `default` session. Calls with the same ID run sequentially; different IDs can run concurrently.

Variables, modules, and the current location are retained per runspace within the external worker process. However, session state is ephemeral: calling `reset_powershell_session` or encountering an unexpected worker process termination causes complete state loss, recreating the session from a clean initial state.

Each session has a separate PowerShell worker process. Changes to process environment variables (`$env:`) remain in that worker and its children; they do not update another session or the MCP server. Files, user/machine environment settings, and other operating system resources remain shared. This separation is not a security boundary.

There are at most six sessions, including `default`. Idle non-default sessions expire after 30 minutes and are cleaned up approximately once a minute. Running or waiting sessions are not removed.

`execute_powershell` accepts an existing absolute `workingDirectory` for one call. Its default timeout is 60 seconds; the build default is 120 seconds and test runner defaults are 180 seconds. Execution timeouts are clamped to 1 through 300 seconds.

`maxOutputChars` limits `output` to 1,000 through 50,000 characters, with a default of 6,000. When output is truncated, `data` is null and the full log is saved locally. This limit does not bound the full JSON response or memory used by a script. Return only the data you need.

Set `compactResponse` to `true` to omit `output` when it exactly duplicates `data`. The default is `false`; truncated or differing output is retained. The effect on model tokens and credits has not been measured for complete chats; do not assert unmeasured token or cost savings.

Python executables invoked without pipeline input receive closed standard input. Existing pipelines are preserved. Detection applies to command names that resolve to executables before execution, such as `python`, `python3`, and `py`; dynamic names and aliases changed during execution are not guaranteed. Interactive Python input is not supported.

Build and test runner diagnostic responses are bounded to 8,000 characters. Nonzero exit codes do not indicate success. Missing or invalid reports return `report_unavailable` or `error`. Inspect the full log for MSBuild failures outside compiler diagnostics. The MCP SDK may report argument type errors as tool errors rather than JSON-RPC `InvalidParams`.

Playwright is resolved from the target directory or its parents; dependencies are not installed with `npx`. The JSON reporter is managed by the tool. Trace inspection has limits on input, event count, DOM depth, and output. It does not replace visual testing of canvas, video, rendering, or CSS. Sensitive URL parameters are masked, but other secrets may remain.

The test runner tools manage their report options: `run_bun_test` rejects `--reporter*`, `run_pytest` rejects `--junitxml` and `--junit-xml`, and `run_vitest` rejects `--reporter` and `--outputFile`. Pass other arguments, such as test paths or filters, through `testArgs`. `run_pytest` uses `pythonPath` when given (an existing absolute path), otherwise the nearest `.venv\Scripts\python.exe` in the working directory or its parents, otherwise `python` on `PATH`; pytest exit code 5 is reported as `no_tests`. Vitest is resolved from the target directory or its parents and always runs once, not in watch mode. JUnit XML is read with DTD processing disabled. Reports larger than 50 MB return `report_unavailable`, and at most 10 failures are listed.

## Safety and logs

Scripts run with the current user's permissions. InazumaPwsh is not a security sandbox, and syntax checks do not restrict unauthorized system operations. Connect only trusted local clients, and do not put credentials in scripts. Avoid interactive commands, GUI prompts, authentication prompts, and elevation requests.

Cancellation and timeouts request PowerShell to stop. An uninterruptible .NET call can continue after the response; its session remains unavailable while stopping. After a 10-second stop grace period, the server terminates the worker and its Windows Job Object process tree; session state is lost. Diagnostic tools also use a Windows Job Object on cancellation or timeout. The extension requests shutdown when its parent extension host exits.

Full logs are saved as UTF-8 under `%TEMP%/inazuma-pwsh-logs/`. `fullLogPath` identifies an execution log; `fullReportPath`, when present, identifies detailed structured diagnostics. Logs are not deleted automatically and may contain sensitive output. Review and remove them when no longer needed.

## Build and verification

The source targets .NET 10 and ModelContextProtocol 2.4.0. The Windows build is self-contained and single-file; its first launch extracts assemblies to a cache. Trimming and Native AOT are disabled. The worker script `powershell-worker.ps1` is deployed alongside the binary. NuGet audits direct and transitive dependencies during restore; review the audit and the latest runtime security updates before release.

From the project directory:

```powershell
dotnet build InazumaPwsh.csproj --nologo -v:q
node test-mcp.mjs
$sourceRoot = (Get-Location).Path
dotnet publish InazumaPwsh.csproj -c Release -p:RuntimeFrameworkVersion=10.0.15 "-p:PathMap=$sourceRoot=/_/inazuma-pwsh" -o artifacts/win-x64-0.3.0 --nologo -v:q
node test-mcp.mjs artifacts/win-x64-0.3.0/InazumaPwsh.exe
```

Reproducibility requires the same source, SDK, runtime, dependencies, and build settings. Path mapping alone is not a guarantee across builds.

Prefer the default extraction cache. If you set `DOTNET_BUNDLE_EXTRACT_BASE_DIR`, use a short absolute Windows path with backslashes. In candidate tests, forward-slash values failed to start CoreCLR with `0x80070057`, and long extraction paths failed while writing bundled module files. The chosen cache must also be writable by the current user.

The tests use temporary projects and include mock Playwright and Vitest CLIs and a fake pytest interpreter. A mock test does not establish that a real browser workflow passed. The real `bun test` check is skipped when Bun is unavailable, and the real pytest check runs only when `INAZUMA_TEST_PYTHON` points to a Python executable with pytest installed. Python tests are skipped when Python is unavailable.