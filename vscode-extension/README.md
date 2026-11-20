<p align="center"><img src="https://raw.githubusercontent.com/furukawa658/inazuma-pwsh/v0.5.1/vscode-extension/media/icon.png" alt="Zuma MCP icon" width="128"></p>

# Zuma MCP

**Your coding agent reads 7% of a design document and 1% of a build log, not 100%.**

![Before/After: full read vs Zuma MCP. 68,000 chars -> 7%, 137,000 chars -> 1%](https://raw.githubusercontent.com/furukawa658/inazuma-pwsh/v0.5.1/vscode-extension/media/before-after.gif)

| Input | Full read | With Zuma MCP | Characters returned |
| --- | --- | --- | --- |
| 68,000-character Markdown design document | 68,000 | `peek_file` | **7%** (about 4,760) |
| same document | 68,000 | `peek_file` + one `read_section` | **20%** (about 13,600) |
| 137,000-character execution log | 137,000 | `summarize_log` | **1%** (about 1,370) |

These are single-file measurements. Savings across complete chats have not been measured.

Zuma MCP (formerly InazumaPwsh) connects your VS Code coding agent to persistent local PowerShell 7 sessions. Reuse variables, modules, and your working location across tool calls; investigate .NET build, TypeScript (`tsc`), Playwright, bun test, pytest, and Vitest failures through structured diagnostics; and read large files and logs by outline, section, or summary, with full logs available when you need the details.

## Why use Zuma MCP?

- **Set up once, continue working.** Keep a runspace ready for the next command instead of repeating imports and recreating variables on every call.
- **Give each chat its own working state.** Separate session IDs keep variables, modules, locations, and worker process environments independent. Different sessions can execute concurrently; files and other operating system resources remain shared.
- **Keep long output out of the conversation.** Bounded responses and optional duplicate-output removal keep routine results concise. Full logs remain available for follow-up searches. Token and credit savings have not been measured across complete chats.
- **Start troubleshooting with evidence.** Retrieve compiler diagnostics, test failure summaries, attachment paths, and trace findings without manually copying entire terminal logs into chat.
- **Automatic MCP integration.** The extension registers its MCP server automatically in VS Code. It bundles the server binary and worker script (`powershell-worker.ps1`), launching external PowerShell 7 workers on demand. Project-specific SDKs and test dependencies remain required.

## Tools

### Persistent shell

| Tool | Purpose |
| --- | --- |
| `execute_powershell` | Run a script in a persistent session and return structured data, output, errors, exit code, and a full-log path when truncated. |
| `reset_powershell_session` | Recreate one worker and its session environment. It does not undo changes to files or user/machine environment settings. |

### Structured diagnostics

| Tool | Purpose |
| --- | --- |
| `run_dotnet_build` | Run a build and return the exit code and SARIF compiler diagnostics. Warnings are omitted by default. |
| `run_dotnet_test` | Run `dotnet test` and return test counts and failures with file, line, message, and stack from its TRX reports. |
| `run_affected_tests` | Select tests from Git changes, untracked files, naming conventions, and relative imports, then run each detected runner once. |
| `run_playwright` | Run locally installed Playwright and return test counts, failures, and attachment paths from its JSON report. |
| `inspect_playwright_trace` | Inspect a trace ZIP for failed actions, HTTP and console errors, and a DOM excerpt. |
| `run_bun_test` | Run `bun test` and return test counts and failures with file, line, message, and stack from its JUnit XML report. |
| `run_pytest` | Run pytest and return test counts and failures with file, line, and message from its JUnit XML report. |
| `run_vitest` | Run locally installed Vitest once (`vitest run`) and return test counts, failures, and file-level errors from its JSON report. |
| `run_tsc` | Run the locally installed TypeScript compiler (`tsc --noEmit`) and return diagnostics with file, line, column, code, and message. |
| `run_eslint` | Run the locally installed ESLint and return counts, per-rule counts, and problems with file, line, column, rule, and message. |
| `run_cargo_check` | Run `cargo check` and return Rust compiler errors and warnings with file, line, column, code, and message. |

### File and log reading

| Tool | Purpose |
| --- | --- |
| `peek_file` | Return a text file's type, encoding, line count, outline with line ranges, and a `FULL` or `PARTIAL` reading recommendation. |
| `read_section` | Return one outline entry by title search, or a line range, with line numbers and a `nextLine` for continuation. |
| `read_symbol` | Return one exact named declaration from a code file, including decorators and the complete symbol range. Overloads can be selected with `occurrence`. |
| `summarize_log` | Group matching log lines with counts and context, and return the last lines of the log. |
| `search_files` | Search text files under a directory with a regex and return per-file match counts and the first matching lines. |

### Rust file tools

Every packaged platform starts the Rust MCP front end. It handles UTF-8 Markdown and code outlines, explicit line ranges, Git-aware `search_files`, and `summarize_log` without starting the .NET compatibility core. PowerShell, diagnostics, symbol reads, and encoding fallback continue through the self-contained C# core.

### Asynchronous tool calls

Clients using MCP protocol revision `2026-07-28` and the `io.modelcontextprotocol/tasks` extension can request PowerShell, build, test, lint, and type-check tools as MCP Tasks. Clients without Tasks opt-in retain synchronous responses. File-reading, log, and trace tools remain synchronous.

## Try it in a chat

After installing the extension and approving its MCP tools, ask your agent to use Zuma MCP. For example:

- "Use a dedicated Zuma MCP session for this chat. Load my module, then reuse it for the next commands."
- "Build this .NET project with Zuma MCP. Show the compiler errors first and consult the full log if the build fails outside compilation."
- "Run this project's Playwright tests and summarize the failures and available trace paths."
- "Inspect this Playwright trace for failed actions, HTTP errors, and console errors."
- "Run the pytest suite with Zuma MCP and fix the first failure."
- "Run `bun test` with Zuma MCP and show me the failing files and lines."
- "Run Vitest with Zuma MCP for `src/utils` only and explain why the import failed."
- "Peek at this design document with Zuma MCP, then read only the section about approvals."
- "Read the `RunAsync` symbol in this C# file, then fix the cancellation path."
- "Summarize the errors in this build log with Zuma MCP before opening it."

The agent must choose and reuse a unique `sessionId` to keep chat state separate. Trace inspection complements browser-based visual checks; it does not verify how a page looks.

## What's new in 0.9.0

- Every packaged platform starts the Rust front end. UTF-8 code outlines, Git-aware search, and log summaries run locally, while the C# compatibility core retains PowerShell, diagnostics, symbol reads, and non-UTF-8 fallback.
- Long-running PowerShell, build, test, lint, and type-check calls can return MCP Tasks for polling and cancellation. Clients without Tasks opt-in keep synchronous responses.
- `read_symbol` reads exact declarations and resolves overloads with `occurrence`. Test and diagnostic tools add affected-test selection, new-diagnostic filtering, regression-only reports, Rust checks, and bounded failure summaries.

## What's new in 0.8.2

This patch fixes the Windows Rust MCP front end so `read_section.ranges` declares its array item schema and VS Code can validate all registered tools.

## 0.8.1

This patch fixes Linux and macOS packaging so all platform variants can be published consistently.

## 0.8.0

On Windows x64, the Marketplace package starts a 0.5 MiB Rust MCP front end. UTF-8 Markdown outlines and explicit line ranges run locally in Rust. The existing .NET server is included as a compatibility process and starts on demand for persistent PowerShell, diagnostics, search, encoding fallback, and other tool paths. All 16 tool names and responses remain available. The other four platform packages continue to use the .NET server in 0.8.0.

## What's new in 0.7.0

This release changes response formats to reduce repeated output:

- `peek_file.entries` now contains `"start-end level title"` strings instead of objects with `start`, `end`, `level`, and `title`.
- `summarize_log` keeps each matched line as `text` at `firstLine`, without repeating it in `context`.
- `codeContext` skips lines already returned for an earlier item in the same file, keeping each item's own line. `run_tsc` omits raw compiler lines from `output` when `diagnostics` already contains their full message.

## What's new in 0.6.x

### 0.6.1

- The MCP server is shown as `Zuma` in VS Code.
- PowerShell outlines use PowerShell's parser without executing the file. Markdown outlines respect the length of fenced code blocks. PowerShell outline queries start a separate PowerShell process and may take longer.

### 0.6.0

#### See how much you save

![Savings in the status bar: peek_file 93%, read_section 89%, summarize_log 97%, 95% in total](https://raw.githubusercontent.com/furukawa658/inazuma-pwsh/v0.6.0/vscode-extension/media/savings.gif)

The status bar now shows what Zuma MCP saved, for example `95% saved`. Click it, or run **Zuma MCP: Show Savings**, for the breakdown by tool: number of calls, bytes read, and bytes returned.

- The status bar shows recorded calls and the savings measured for diagnostic tools, `peek_file`, `read_section`, and `summarize_log`. The records are stored in `%TEMP%/inazuma-pwsh-logs/usage.jsonl`.
- Calls without a comparable source size, including `execute_powershell` and `search_files`, are counted but excluded from the savings percentage. The percentage is not a token count or a whole-chat measurement.
- The numbers in the animation were measured with 0.6.0 on this repository: a 44 KB C# file (`peek_file`, `read_section`) and a 170 KB build log (`summarize_log`). The animation is an illustration, not a screen recording.

#### New tools and options

- **`run_dotnet_test`.** Runs `dotnet test` and returns test counts and failures with file, line, message, and stack from its TRX reports. VSTest is verified with a mock project; the Microsoft.Testing.Platform path (selected by `global.json`) has not been run against a real project.
- **`run_eslint`.** Runs the locally installed ESLint and returns error and warning counts, per-rule counts, and the first 15 problems.
- **`search_files`.** Searches text files under a directory with a regex and returns per-file match counts and the first matching lines in at most 8,000 characters. It skips `.git`, `node_modules`, `.venv`, binary files, and files over 5 MB, and respects Git ignore rules.
- **`contextLines` for `run_dotnet_build`.** Compiler errors can carry `codeContext` like the test tools. `codeContext` is added only for files under `workingDirectory`.
- **`rerunArgs`.** Failed test runs return arguments that rerun only the failures: failing files for `run_bun_test` and `run_vitest`, `--lf` for `run_pytest`, `--last-failed` for `run_playwright`, and a `--filter` expression for `run_dotnet_test` (VSTest). It is omitted when the report does not list every failure.
- **Same-cause failures are grouped.** `run_bun_test`, `run_pytest`, `run_vitest`, and `run_dotnet_test` report failures at the same file and line with the same message once, with `count`.
- **`run_tsc` additions.** `onlyNew: true` lists only diagnostics that the previous run with the same `workingDirectory` and `tscArgs` did not have, with `newCount` and `fixedCount`; the previous result is kept in memory until the server stops. `--build` and `-b` are accepted and omit `--noEmit`.

Release notes for all versions are in [CHANGELOG.md](https://github.com/furukawa658/inazuma-pwsh/blob/v0.8.2/CHANGELOG.md).

## Requirements and installation

- VS Code 1.101 or later. Windows x64 is the verified platform; Linux (x64, arm64) and macOS (x64, arm64) are supported but experimental (see "Notes").
- External PowerShell 7 (`pwsh.exe` on Windows, `pwsh` on Linux/macOS) installed on the system (only PowerShell 7.7.0 has been verified in practice). The server searches `PATH` first, then the standard install locations (Windows: `%ProgramFiles%\PowerShell\7`, `%LOCALAPPDATA%\Microsoft\WindowsApps`, `%USERPROFILE%\.dotnet\tools`; Linux/macOS: `/opt/homebrew/bin`, `/usr/local/bin`, `/usr/bin`, `/snap/bin`, `~/.dotnet/tools`), or you can specify an explicit executable path via the `inazumaPwsh.pwshPath` setting or `INAZUMA_PWSH_PATH` environment variable. An invalid explicit path fails immediately without fallback.
- The extension bundles a Rust MCP server, a self-contained C# compatibility core, and the worker script (`powershell-worker.ps1`) for every platform. The worker script runs under the external PowerShell 7 process to manage session runspaces.
- A trusted workspace. The extension runs in the local extension host, including when a remote workspace is open; it does not switch to the remote shell or filesystem.
- Install the VSIX for your platform (win32-x64, linux-x64, linux-arm64, darwin-x64 or darwin-arm64) using **Extensions: Install from VSIX** or from the Visual Studio Code Marketplace. The extension registers its MCP server automatically; no `mcp.json` entry is required. Follow VS Code's prompts to trust the workspace, start the server, and approve tool calls.
- Build diagnostics require the .NET SDK; Playwright diagnostics require Node.js and a locally installed Playwright package. `run_bun_test` requires Bun on `PATH`, `run_pytest` requires a Python interpreter with pytest installed, and `run_vitest` requires Node.js and a locally installed Vitest package.

The Zuma MCP project code is licensed under the MIT License, copyright 2026 Shunsuke F. Bundled third-party components retain their own licenses; see `LICENSE` and `THIRD-PARTY-NOTICES.txt` for details. Repository: https://github.com/furukawa658/inazuma-pwsh

## Notes

- Zuma MCP was formerly InazumaPwsh; the extension ID (`ShunsukeF.inazuma-pwsh`), setting keys (`inazumaPwsh.*`), environment variables, executable names, and the log folder keep their inazuma names for compatibility.
- Linux and macOS are experimental. On Linux/macOS there is no Job Object, so workers may be orphaned if the server process crashes.
- Details on sessions, output limits, cancellation, and building from source are in [docs/reference.md](https://github.com/furukawa658/inazuma-pwsh/blob/v0.8.2/docs/reference.md).

### Sessions and output

- Choose a unique `sessionId` for each chat and reuse it within that chat. If omitted, calls share the `default` session. Calls with the same ID run sequentially; different IDs can run concurrently.
- Variables, modules, and the current location are retained per session until `reset_powershell_session` or an unexpected worker process termination. This separation is not a security boundary.
- There are at most six sessions, including `default`. Idle sessions expire after 10 minutes.
- `maxOutputChars` limits `output` to 1,000 through 50,000 characters, with a default of 6,000. When output is truncated, the full log is saved locally.

Timeouts, `compactResponse`, Python standard input, and the limits of each diagnostic tool are described in [docs/reference.md](https://github.com/furukawa658/inazuma-pwsh/blob/v0.8.0/docs/reference.md).

### Safety and logs

Scripts run with the current user's permissions. Zuma MCP is not a security sandbox, and syntax checks do not restrict unauthorized system operations. Connect only trusted local clients, and do not put credentials in scripts. Avoid interactive commands, GUI prompts, authentication prompts, and elevation requests.

Full logs are saved as UTF-8 under `%TEMP%/inazuma-pwsh-logs/`. `fullLogPath` identifies an execution log; `fullReportPath`, when present, identifies detailed structured diagnostics. Logs older than 3 days, or beyond 200 MB in total, are deleted at server startup, and they may contain sensitive output. Review and remove them when no longer needed.

Cancellation and timeout behavior is described in [docs/reference.md](https://github.com/furukawa658/inazuma-pwsh/blob/v0.8.0/docs/reference.md).

### Build and verification

Build commands, reproducibility notes, and the limits of the test suite are in [docs/reference.md](https://github.com/furukawa658/inazuma-pwsh/blob/v0.8.0/docs/reference.md).
