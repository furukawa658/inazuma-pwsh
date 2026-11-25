#![recursion_limit = "256"]

mod file_tools;
mod handoff;
mod read_guard;
mod sidecar;
mod task_anchor;

use read_guard::RedundantReadGuard;
use serde::Deserialize;
use serde_json::{Value, json};
use sidecar::Sidecar;
use std::env;
use std::fs::{self, OpenOptions};
use std::io::{self, BufRead, Write};
use std::path::{Path, PathBuf};
use std::sync::{Arc, Mutex, OnceLock};
use std::time::{Duration, SystemTime, UNIX_EPOCH};

const VERSION: &str = env!("CARGO_PKG_VERSION");

#[cfg(windows)]
unsafe extern "system" {
    fn OpenProcess(
        desired_access: u32,
        inherit_handle: i32,
        process_id: u32,
    ) -> *mut std::ffi::c_void;
    fn GetExitCodeProcess(process: *mut std::ffi::c_void, exit_code: *mut u32) -> i32;
    fn CloseHandle(object: *mut std::ffi::c_void) -> i32;
}

#[cfg(unix)]
unsafe extern "C" {
    fn kill(process_id: i32, signal: i32) -> i32;
}

fn parent_pid() -> Result<Option<u32>, String> {
    let mut args = env::args();
    while let Some(argument) = args.next() {
        if argument != "--parent-pid" {
            continue;
        }
        let value = args
            .next()
            .ok_or("--parent-pid requires a positive process ID.")?;
        let process_id = value
            .parse::<u32>()
            .map_err(|_| "--parent-pid requires a positive process ID.")?;
        if process_id == 0 {
            return Err("--parent-pid requires a positive process ID.".to_string());
        }
        return Ok(Some(process_id));
    }
    Ok(None)
}

#[cfg(windows)]
fn process_is_alive(process_id: u32) -> bool {
    const PROCESS_QUERY_LIMITED_INFORMATION: u32 = 0x1000;
    const STILL_ACTIVE: u32 = 259;
    let handle = unsafe { OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, 0, process_id) };
    if handle.is_null() {
        return false;
    }
    let mut exit_code = 0;
    let alive =
        unsafe { GetExitCodeProcess(handle, &mut exit_code) } != 0 && exit_code == STILL_ACTIVE;
    unsafe { CloseHandle(handle) };
    alive
}

#[cfg(unix)]
fn process_is_alive(process_id: u32) -> bool {
    unsafe { kill(process_id as i32, 0) == 0 }
}

fn watch_parent(process_id: u32) {
    std::thread::spawn(move || {
        while process_is_alive(process_id) {
            std::thread::sleep(Duration::from_millis(100));
        }
        std::process::exit(0);
    });
}

struct Bridge {
    sidecar: Mutex<Option<Arc<Sidecar>>>,
    task_sidecar: Mutex<Option<Arc<Sidecar>>>,
    task_anchors: task_anchor::TaskAnchorStore,
    read_guard: RedundantReadGuard,
}

impl Bridge {
    fn new() -> Self {
        Self {
            sidecar: Mutex::new(None),
            task_sidecar: Mutex::new(None),
            task_anchors: task_anchor::TaskAnchorStore::new(),
            read_guard: RedundantReadGuard::new(),
        }
    }

    fn sidecar(&self) -> Result<Arc<Sidecar>, String> {
        let mut sidecar = self.sidecar.lock().unwrap();
        if sidecar.is_none() {
            *sidecar = Some(Arc::new(Sidecar::start()?));
        }
        Ok(Arc::clone(sidecar.as_ref().unwrap()))
    }

    fn task_sidecar(&self) -> Result<Arc<Sidecar>, String> {
        let mut sidecar = self.task_sidecar.lock().unwrap();
        if sidecar.is_none() {
            *sidecar = Some(Arc::new(Sidecar::start_for_tasks()?));
        }
        Ok(Arc::clone(sidecar.as_ref().unwrap()))
    }

    fn request_for(&self, client_id: &Value, method: &str, params: Value) -> Result<Value, String> {
        self.sidecar()?.request_for(Some(client_id), method, params)
    }

    fn task_request_for(
        &self,
        client_id: &Value,
        method: &str,
        params: Value,
    ) -> Result<Value, String> {
        self.task_sidecar()?
            .request_for(Some(client_id), method, params)
    }

    fn cancel(&self, params: &Value) -> Result<(), String> {
        if let Some(sidecar) = self.sidecar.lock().unwrap().as_ref() {
            sidecar.cancel(params)?;
        }
        Ok(())
    }

    fn notify(&self, method: &str, params: Value) -> Result<(), String> {
        if let Some(sidecar) = self.sidecar.lock().unwrap().as_ref() {
            sidecar.notify(method, params)?;
        }
        Ok(())
    }
}

#[derive(Deserialize)]
struct Request {
    id: Option<Value>,
    method: String,
    #[serde(default)]
    params: Value,
}

#[derive(Clone)]
struct OutlineEntry {
    start: usize,
    end: usize,
    level: usize,
    title: String,
}

fn string_argument<'a>(arguments: &'a Value, name: &str) -> Result<&'a str, String> {
    arguments
        .get(name)
        .and_then(Value::as_str)
        .ok_or_else(|| format!("{name} must be a string"))
}

fn integer_argument(arguments: &Value, name: &str, default: usize) -> usize {
    arguments
        .get(name)
        .and_then(Value::as_u64)
        .map(|value| value as usize)
        .unwrap_or(default)
}

fn determine_kind(path: &Path) -> &'static str {
    match path
        .extension()
        .and_then(|extension| extension.to_str())
        .unwrap_or("")
        .to_ascii_lowercase()
        .as_str()
    {
        "md" | "mdx" | "markdown" => "markdown",
        "cs" | "vb" | "ts" | "tsx" | "js" | "jsx" | "mjs" | "cjs" | "ps1" | "psm1" | "py"
        | "go" | "rs" | "java" | "kt" => "code",
        "json" | "jsonc" => "json",
        "yml" | "yaml" => "yaml",
        _ => "text",
    }
}

fn read_lines(path: &Path) -> Result<Vec<String>, String> {
    let content = fs::read_to_string(path).map_err(|error| error.to_string())?;
    Ok(content.lines().map(str::to_owned).collect())
}

fn record_usage(tool: &str, source: &Path, response: &str) {
    static GATE: OnceLock<Mutex<()>> = OnceLock::new();
    let directory = env::temp_dir().join("inazuma-pwsh-logs");
    let path = directory.join("usage.jsonl");
    let time = SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .map(|duration| duration.as_millis())
        .unwrap_or(0);
    let source_bytes = fs::metadata(source)
        .map(|metadata| metadata.len())
        .unwrap_or(0);
    let entry = if tool == "search_files" || source_bytes == 0 {
        json!({ "time": time, "tool": tool, "countOnly": true })
    } else {
        json!({ "time": time, "tool": tool, "sourceBytes": source_bytes, "responseBytes": response.len() })
    };

    let _guard = GATE.get_or_init(|| Mutex::new(())).lock().unwrap();
    let result = (|| -> io::Result<()> {
        fs::create_dir_all(&directory)?;
        if fs::metadata(&path)
            .map(|metadata| metadata.len())
            .unwrap_or(0)
            > 5 * 1024 * 1024
        {
            fs::remove_file(&path)?;
        }
        let mut file = OpenOptions::new().create(true).append(true).open(path)?;
        writeln!(file, "{entry}")?;
        Ok(())
    })();
    let _ = result;
}

fn validate_pwsh_environment() -> Result<PathBuf, String> {
    let executable = if cfg!(windows) { "pwsh.exe" } else { "pwsh" };
    if let Some(explicit) = env::var_os("INAZUMA_PWSH_PATH")
        && !explicit.is_empty()
    {
        let path = PathBuf::from(explicit);
        if path.is_file() {
            return Ok(path.canonicalize().unwrap_or(path));
        }
        return Err(format!(
            "The specified INAZUMA_PWSH_PATH does not exist: {}. Fix or clear inazumaPwsh.pwshPath (INAZUMA_PWSH_PATH).",
            path.display()
        ));
    }

    let mut directories = env::var_os("PATH")
        .map(|path| env::split_paths(&path).collect::<Vec<_>>())
        .unwrap_or_default();
    if cfg!(windows) {
        if let Some(program_files) = env::var_os("ProgramFiles") {
            directories.push(PathBuf::from(program_files).join("PowerShell/7"));
        }
        if let Some(local_app_data) = env::var_os("LOCALAPPDATA") {
            directories.push(PathBuf::from(local_app_data).join("Microsoft/WindowsApps"));
        }
        if let Some(profile) = env::var_os("USERPROFILE") {
            directories.push(PathBuf::from(profile).join(".dotnet/tools"));
        }
    } else {
        for path in [
            "/opt/homebrew/bin",
            "/usr/local/bin",
            "/usr/bin",
            "/snap/bin",
        ] {
            directories.push(PathBuf::from(path));
        }
        if let Some(home) = env::var_os("HOME") {
            directories.push(PathBuf::from(home).join(".dotnet/tools"));
        }
    }

    for directory in directories {
        let path = directory.join(executable);
        if path.is_file() {
            return Ok(path.canonicalize().unwrap_or(path));
        }
    }
    Err(format!(
        "Could not locate {executable} in PATH or the standard install locations. PowerShell 7 or higher is required. Install it, restart VS Code, or set inazumaPwsh.pwshPath (INAZUMA_PWSH_PATH) to the absolute path of {executable}."
    ))
}

fn markdown_outline(lines: &[String]) -> Vec<OutlineEntry> {
    let mut entries = Vec::new();
    let mut in_fence = false;
    let mut fence_marker = ' ';
    let mut fence_length = 0;

    for (index, line) in lines.iter().enumerate() {
        let trimmed = line.trim_start();
        if in_fence {
            let marker_length = trimmed
                .chars()
                .take_while(|character| *character == fence_marker)
                .count();
            if marker_length >= fence_length && trimmed[marker_length..].trim().is_empty() {
                in_fence = false;
            }
            continue;
        }

        if trimmed.starts_with("```") || trimmed.starts_with("~~~") {
            in_fence = true;
            fence_marker = trimmed.chars().next().unwrap_or('`');
            fence_length = trimmed
                .chars()
                .take_while(|character| *character == fence_marker)
                .count();
            continue;
        }

        let hashes = line
            .chars()
            .take_while(|character| *character == '#')
            .count();
        if (1..=6).contains(&hashes) && line[hashes..].starts_with(char::is_whitespace) {
            let title = line[hashes..]
                .trim()
                .trim_end_matches('#')
                .trim()
                .chars()
                .take(120)
                .collect();
            entries.push(OutlineEntry {
                start: index + 1,
                end: index + 1,
                level: hashes,
                title,
            });
        }
    }

    for index in 0..entries.len() {
        entries[index].end = lines.len();
        if let Some(next) = entries[index + 1..]
            .iter()
            .find(|entry| entry.level <= entries[index].level)
        {
            entries[index].end = next.start - 1;
        }
    }
    entries
}

fn text_outline(lines: &[String]) -> Vec<OutlineEntry> {
    (0..lines.len())
        .step_by(100)
        .map(|start| {
            let line = start + 1;
            let end = (start + 100).min(lines.len());
            OutlineEntry {
                start: line,
                end,
                level: 1,
                title: format!("lines {line}-{end}"),
            }
        })
        .collect()
}

fn peek_file(arguments: &Value) -> Value {
    let path_text = match string_argument(arguments, "path") {
        Ok(path) => path,
        Err(message) => return json!({ "status": "error", "message": message }),
    };
    let path = Path::new(path_text);
    if !path.is_absolute() || !path.is_file() {
        return json!({ "status": "error", "message": file_tools::path_problem(path, "file") });
    }

    let lines = match read_lines(path) {
        Ok(lines) => lines,
        Err(message) => return json!({ "status": "error", "message": message }),
    };
    let kind = determine_kind(path);
    let file_bytes = fs::metadata(path).map(|metadata| metadata.len()).unwrap_or(0);
    let mut entries = if kind == "markdown" {
        markdown_outline(&lines)
    } else if kind == "code" {
        file_tools::code_outline(
            &lines,
            path.extension()
                .and_then(|value| value.to_str())
                .is_some_and(|value| value.eq_ignore_ascii_case("py")),
        )
    } else {
        text_outline(&lines)
    };
    if entries.is_empty() && !lines.is_empty() {
        entries = text_outline(&lines);
    }
    let max_entries = integer_argument(arguments, "maxEntries", 200).clamp(10, 1000);
    if let Some(max_level) = arguments.get("maxLevel").and_then(Value::as_u64) {
        entries.retain(|entry| entry.level <= max_level as usize);
    }
    let entries_truncated = entries.len() > max_entries;
    let rendered: Vec<String> = entries
        .into_iter()
        .take(max_entries)
        .map(|entry| {
            format!(
                "{}-{} {} {}",
                entry.start, entry.end, entry.level, entry.title
            )
        })
        .collect();

    json!({
        "status": "success",
        "path": path_text,
        "kind": kind,
        "encoding": "utf-8",
        "lines": lines.len(),
        "bytes": file_bytes,
        "recommend": if lines.len() <= integer_argument(arguments, "fullThreshold", 200).clamp(1, 5000) && file_bytes <= 20_000 { "FULL" } else { "PARTIAL" },
        "entries": rendered,
        "entriesTruncated": entries_truncated
    })
}

fn read_section(arguments: &Value) -> Value {
    let path_text = match string_argument(arguments, "path") {
        Ok(path) => path,
        Err(message) => return json!({ "status": "error", "message": message }),
    };
    let path = Path::new(path_text);
    if !path.is_absolute() || !path.is_file() {
        return json!({ "status": "error", "message": file_tools::path_problem(path, "file") });
    }
    let Some(start) = arguments
        .get("startLine")
        .and_then(Value::as_u64)
        .map(|value| value as usize)
    else {
        return json!({ "status": "error", "message": "Specify startLine for the Rust line-range fast path." });
    };
    let end = arguments
        .get("endLine")
        .and_then(Value::as_u64)
        .map(|value| value as usize)
        .unwrap_or(start + 199);
    if start > end {
        return json!({ "status": "error", "message": "startLine must not be greater than endLine." });
    }

    let lines = match read_lines(path) {
        Ok(lines) => lines,
        Err(message) => return json!({ "status": "error", "message": message }),
    };
    if lines.is_empty() {
        return json!({ "status": "success", "path": path_text, "encoding": "utf-8", "totalLines": 0, "startLine": 1, "endLine": 0, "truncated": false, "nextLine": null, "text": "" });
    }
    let actual_start = start.clamp(1, lines.len());
    let actual_end = end.clamp(actual_start, lines.len());
    let max_chars = integer_argument(arguments, "maxChars", 12000).clamp(1000, 50000);
    let mut text = String::new();
    let mut last_line = actual_start;
    let mut next_line = None;
    for line_number in actual_start..=actual_end {
        let formatted = format!("{line_number}: {}", lines[line_number - 1]);
        let separator = usize::from(!text.is_empty());
        if text.len() + separator + formatted.len() > max_chars {
            next_line = Some(line_number);
            break;
        }
        if !text.is_empty() {
            text.push('\n');
        }
        text.push_str(&formatted);
        last_line = line_number;
    }

    json!({
        "status": "success",
        "path": path_text,
        "encoding": "utf-8",
        "totalLines": lines.len(),
        "startLine": actual_start,
        "endLine": last_line,
        "truncated": next_line.is_some(),
        "nextLine": next_line,
        "text": text
    })
}

fn tool_definitions() -> Value {
    json!({ "tools": [
        { "name": "zuma_task", "description": "Record the goal, acceptance criteria, out-of-scope items, and allowed paths of the current task (operation set, get, clear). Call set at the start of a substantial task and clear when it is finished.", "inputSchema": { "type": "object", "properties": { "operation": { "type": "string", "enum": ["set", "get", "clear"] }, "goal": { "type": "string" }, "target": { "type": "string" }, "acceptanceCriteria": { "type": "array", "items": { "type": "string" } }, "outOfScope": { "type": "array", "items": { "type": "string" } }, "allowedPaths": { "type": "array", "items": { "type": "string" } }, "sessionId": { "type": "string" } }, "required": ["operation"] } },
        { "name": "zuma_shell", "description": "Run PowerShell commands in a persistent session that keeps the working directory and variables. Use this instead of the generic terminal tool for any shell command; output is size-bounded and the full log path is returned. Operation: execute, reset_session, or list_sessions.", "inputSchema": { "type": "object", "properties": { "operation": { "type": "string", "enum": ["execute", "reset_session", "list_sessions"] }, "script": { "type": "string" }, "timeoutSeconds": { "type": "integer" }, "workingDirectory": { "type": "string" }, "maxOutputChars": { "type": "integer" }, "sessionId": { "type": "string" }, "compactResponse": { "type": "boolean" } }, "required": ["operation"] } },
        { "name": "zuma_execute", "description": "Run builds, tests, and linters and get a compact result (failing tests and new diagnostics only) instead of raw terminal output. Use this instead of running dotnet, bun test, pytest, vitest, tsc, eslint, cargo, or playwright in a terminal. Operation selects dotnet_build, dotnet_test, affected_tests (tests for Git-changed files only), playwright, bun_test, pytest, vitest, tsc, eslint, or cargo_check.", "inputSchema": { "type": "object", "properties": { "operation": { "type": "string", "enum": ["dotnet_build", "dotnet_test", "affected_tests", "playwright", "bun_test", "pytest", "vitest", "tsc", "eslint", "cargo_check"] }, "workingDirectory": { "type": "string" }, "testArgs": { "type": "array", "items": { "type": "string" } }, "tscArgs": { "type": "array", "items": { "type": "string" } }, "eslintArgs": { "type": "array", "items": { "type": "string" } }, "cargoArgs": { "type": "array", "items": { "type": "string" } }, "target": { "type": "string" }, "includeWarnings": { "type": "boolean" }, "onlyNew": { "type": "boolean" }, "regressionsOnly": { "type": "boolean" }, "timeoutSeconds": { "type": "integer" }, "contextLines": { "type": "integer" }, "compactResponse": { "type": "boolean" }, "pythonPath": { "type": "string" } }, "required": ["operation", "workingDirectory"] } },
        { "name": "zuma_read", "description": "Read files and logs with bounded output instead of reading whole files. Use before opening a large file or log. Operation selects peek_file (outline of a large file), read_section (line range or heading), read_symbol (one function or class), summarize_log (errors and tail of a log), search_files (regex or literal search across files), or inspect_playwright_trace.", "inputSchema": { "type": "object", "properties": { "operation": { "type": "string", "enum": ["peek_file", "read_section", "read_symbol", "summarize_log", "search_files", "inspect_playwright_trace"] }, "path": { "type": "string" }, "pattern": { "type": "string" }, "symbol": { "type": "string" }, "query": { "type": "string" }, "startLine": { "type": "integer" }, "endLine": { "type": "integer" }, "workingDirectory": { "type": "string" }, "tracePath": { "type": "string" }, "maxChars": { "type": "integer" }, "maxResults": { "type": "integer" }, "maxPerFile": { "type": "integer" }, "contextLines": { "type": "integer" }, "tailLines": { "type": "integer" }, "literal": { "type": "boolean" }, "exclude": { "type": "array", "items": { "type": "string" } }, "ranges": { "type": "array", "items": { "oneOf": [{ "type": "string" }, { "type": "object" }] } } }, "required": ["operation"] } },
        { "name": "zuma_context", "description": "Keep large evidence and working notes outside the chat. Operation selects store_capsule and query_capsule (save a large log or output, then query only the lines needed), semantic_git_diff (declaration-level summary of uncommitted changes), remember and query (store and search facts, decisions, unknowns, and next actions), compile (grouped summary of stored records), or plan_read (rank files to read for a question).", "inputSchema": { "type": "object", "properties": { "operation": { "type": "string", "enum": ["store_capsule", "query_capsule", "semantic_git_diff", "remember", "query", "compile", "plan_read"] }, "kind": { "type": "string" }, "content": { "type": "string" }, "sourcePath": { "type": "string" }, "workingDirectory": { "type": "string" }, "label": { "type": "string" }, "capsuleId": { "type": "string" }, "baseCapsuleId": { "type": "string" }, "query": { "type": "string" }, "key": { "type": "string" }, "value": { "type": "string" }, "evidence": { "type": "string" }, "goal": { "type": "string" }, "baseRef": { "type": "string" }, "path": { "type": "string" }, "onlyUnseen": { "type": "boolean" }, "onlyActive": { "type": "boolean" }, "includeEvidence": { "type": "boolean" }, "paths": { "type": "array", "items": { "type": "string" } }, "maxResults": { "type": "integer" }, "maxRecommendations": { "type": "integer" }, "maxChars": { "type": "integer" } }, "required": ["operation", "workingDirectory"] } },
        { "name": "zuma_gate", "description": "Inspect or change execution-loop protection that blocks repeated unscoped full test runs (operation get, set, reset).", "inputSchema": { "type": "object", "properties": { "operation": { "type": "string", "enum": ["get", "set", "reset"] }, "mode": { "type": "string", "enum": ["manual", "incremental", "milestone"] }, "enabled": { "type": "boolean" }, "repeatFailureThreshold": { "type": "integer" }, "historySeconds": { "type": "integer" }, "skipRedPhase": { "type": "boolean" }, "workspaceKey": { "type": "string" } }, "required": ["operation"] } },
        { "name": "zuma_handoff", "description": "Prepare a bounded summary of a chat session to continue the work in a new chat (operation prepare).", "inputSchema": { "type": "object", "properties": { "operation": { "type": "string", "enum": ["prepare"] }, "sessionPath": { "type": "string" }, "sessionId": { "type": "string" }, "workingDirectory": { "type": "string" }, "includeTitles": { "type": "boolean" }, "nextActionHint": { "type": "string" }, "maxChars": { "type": "integer" } }, "required": ["operation"] } }
    ] })
}

#[allow(dead_code)]
fn legacy_tool_definitions() -> Value {
    json!({ "tools": [
        { "name": "zuma_task", "description": "Manage the current task scope. Use operation set, get, or clear.", "inputSchema": { "type": "object", "properties": { "operation": { "type": "string", "enum": ["set", "get", "clear"] }, "goal": { "type": "string" }, "target": { "type": "string" }, "acceptanceCriteria": { "type": "array", "items": { "type": "string" } }, "outOfScope": { "type": "array", "items": { "type": "string" } }, "allowedPaths": { "type": "array", "items": { "type": "string" } }, "sessionId": { "type": "string" } }, "required": ["operation"] } },
        { "name": "zuma_shell", "description": "Run or manage persistent PowerShell sessions. Use operation execute, reset_session, or list_sessions.", "inputSchema": { "type": "object", "properties": { "operation": { "type": "string", "enum": ["execute", "reset_session", "list_sessions"] }, "script": { "type": "string" }, "timeoutSeconds": { "type": "integer" }, "workingDirectory": { "type": "string" }, "maxOutputChars": { "type": "integer" }, "sessionId": { "type": "string" }, "compactResponse": { "type": "boolean" } }, "required": ["operation"] } },
        { "name": "zuma_execute", "description": "Run builds and static diagnostics. Use operation dotnet_build, dotnet_test, playwright, bun_test, pytest, vitest, tsc, eslint, cargo_check, or affected_tests.", "inputSchema": { "type": "object", "properties": { "operation": { "type": "string", "enum": ["dotnet_build", "dotnet_test", "affected_tests", "playwright", "bun_test", "pytest", "vitest", "tsc", "eslint", "cargo_check"] }, "workingDirectory": { "type": "string" }, "testArgs": { "type": "array", "items": { "type": "string" } }, "tscArgs": { "type": "array", "items": { "type": "string" } }, "eslintArgs": { "type": "array", "items": { "type": "string" } }, "cargoArgs": { "type": "array", "items": { "type": "string" } }, "target": { "type": "string" }, "includeWarnings": { "type": "boolean" }, "onlyNew": { "type": "boolean" }, "regressionsOnly": { "type": "boolean" }, "timeoutSeconds": { "type": "integer" }, "contextLines": { "type": "integer" }, "compactResponse": { "type": "boolean" }, "pythonPath": { "type": "string" } }, "required": ["operation", "workingDirectory"] } },
        { "name": "zuma_read", "description": "Search or read bounded source and log content. Use operation peek_file, read_section, read_symbol, summarize_log, search_files, or inspect_playwright_trace.", "inputSchema": { "type": "object", "properties": { "operation": { "type": "string", "enum": ["peek_file", "read_section", "read_symbol", "summarize_log", "search_files", "inspect_playwright_trace"] }, "path": { "type": "string" }, "pattern": { "type": "string" }, "symbol": { "type": "string" }, "query": { "type": "string" }, "startLine": { "type": "integer" }, "endLine": { "type": "integer" }, "workingDirectory": { "type": "string" }, "tracePath": { "type": "string" }, "maxChars": { "type": "integer" }, "maxResults": { "type": "integer" }, "maxPerFile": { "type": "integer" }, "contextLines": { "type": "integer" }, "tailLines": { "type": "integer" }, "literal": { "type": "boolean" }, "exclude": { "type": "array", "items": { "type": "string" } }, "ranges": { "type": "array", "items": { "oneOf": [{ "type": "string" }, { "type": "object" }] } } }, "required": ["operation"] } },
        { "name": "zuma_context", "description": "Store, query, compile, or plan workspace context and evidence. Use operation store_capsule, query_capsule, semantic_git_diff, remember, query, compile, or plan_read.", "inputSchema": { "type": "object", "properties": { "operation": { "type": "string", "enum": ["store_capsule", "query_capsule", "semantic_git_diff", "remember", "query", "compile", "plan_read"] }, "kind": { "type": "string" }, "content": { "type": "string" }, "sourcePath": { "type": "string" }, "workingDirectory": { "type": "string" }, "label": { "type": "string" }, "capsuleId": { "type": "string" }, "baseCapsuleId": { "type": "string" }, "query": { "type": "string" }, "key": { "type": "string" }, "value": { "type": "string" }, "evidence": { "type": "string" }, "goal": { "type": "string" }, "baseRef": { "type": "string" }, "path": { "type": "string" }, "onlyUnseen": { "type": "boolean" }, "onlyActive": { "type": "boolean" }, "includeEvidence": { "type": "boolean" }, "paths": { "type": "array", "items": { "type": "string" } }, "maxResults": { "type": "integer" }, "maxRecommendations": { "type": "integer" }, "maxChars": { "type": "integer" } }, "required": ["operation", "workingDirectory"] } },
        { "name": "zuma_gate", "description": "Inspect or change execution-loop protection. Use operation get, set, or reset.", "inputSchema": { "type": "object", "properties": { "operation": { "type": "string", "enum": ["get", "set", "reset"] }, "mode": { "type": "string", "enum": ["manual", "incremental", "milestone"] }, "enabled": { "type": "boolean" }, "repeatFailureThreshold": { "type": "integer" }, "historySeconds": { "type": "integer" }, "skipRedPhase": { "type": "boolean" }, "workspaceKey": { "type": "string" } }, "required": ["operation"] } },
        { "name": "zuma_handoff", "description": "Prepare or continue a bounded chat handoff.", "inputSchema": { "type": "object", "properties": { "operation": { "type": "string", "enum": ["prepare"] }, "sessionPath": { "type": "string" }, "sessionId": { "type": "string" }, "workingDirectory": { "type": "string" }, "includeTitles": { "type": "boolean" }, "nextActionHint": { "type": "string" }, "maxChars": { "type": "integer" } }, "required": ["operation"] } },
        { "name": "set_task_anchor", "description": "Set the current chat target and scope. Call once at the start of a substantial task, and update it when the goal or scope changes. Zuma will append this anchor to later tool responses and warn when a path is outside allowedPaths.", "inputSchema": { "type": "object", "properties": { "goal": { "type": "string" }, "target": { "type": "string" }, "acceptanceCriteria": { "type": "array", "items": { "type": "string" } }, "outOfScope": { "type": "array", "items": { "type": "string" } }, "allowedPaths": { "type": "array", "items": { "type": "string" } }, "sessionId": { "type": "string" } }, "required": ["goal", "target"] } },
        { "name": "get_task_anchor", "description": "Return the current target, acceptance criteria, scope, and allowed paths.", "inputSchema": { "type": "object", "properties": {} } },
        { "name": "clear_task_anchor", "description": "Clear the current task anchor. Use this when the task is complete or a new unrelated chat starts.", "inputSchema": { "type": "object", "properties": {} } },
        { "name": "execute_powershell", "description": "Run PowerShell in a persistent session.", "inputSchema": { "type": "object", "properties": { "script": { "type": "string" }, "timeoutSeconds": { "type": "integer" }, "workingDirectory": { "type": "string" }, "maxOutputChars": { "type": "integer" }, "sessionId": { "type": "string" }, "compactResponse": { "type": "boolean" } }, "required": ["script"] } },
        { "name": "reset_powershell_session", "description": "Reset a persistent PowerShell session.", "inputSchema": { "type": "object", "properties": { "sessionId": { "type": "string" } } } },
        { "name": "list_powershell_sessions", "description": "List persistent PowerShell sessions.", "inputSchema": { "type": "object", "properties": {} } },
        { "name": "get_execution_gate", "description": "Return the current execution-gate mode, thresholds, and tripped circuits.", "inputSchema": { "type": "object", "properties": {} } },
        { "name": "set_execution_gate", "description": "Set execution-gate mode and repeat-failure policy.", "inputSchema": { "type": "object", "properties": { "mode": { "type": "string" }, "enabled": { "type": "boolean" }, "repeatFailureThreshold": { "type": "integer" }, "historySeconds": { "type": "integer" }, "skipRedPhase": { "type": "boolean" } }, "required": ["mode"] } },
        { "name": "reset_execution_gate", "description": "Clear repeat-failure history and tripped circuit state.", "inputSchema": { "type": "object", "properties": { "workspaceKey": { "type": "string" } } } },
        { "name": "run_dotnet_build", "description": "Run dotnet build and return structured diagnostics.", "inputSchema": { "type": "object", "properties": { "workingDirectory": { "type": "string" }, "target": { "type": "string" }, "includeWarnings": { "type": "boolean" }, "onlyNew": { "type": "boolean" }, "timeoutSeconds": { "type": "integer" }, "contextLines": { "type": "integer" }, "compactResponse": { "type": "boolean" } }, "required": ["workingDirectory"] } },
        { "name": "run_dotnet_test", "description": "Run dotnet test and return structured failures.", "inputSchema": { "type": "object", "properties": { "workingDirectory": { "type": "string" }, "testArgs": { "type": "array", "items": { "type": "string" } }, "regressionsOnly": { "type": "boolean" }, "timeoutSeconds": { "type": "integer" }, "contextLines": { "type": "integer" }, "compactResponse": { "type": "boolean" } }, "required": ["workingDirectory"] } },
        { "name": "run_affected_tests", "description": "Select and run tests related to tracked and untracked Git changes.", "inputSchema": { "type": "object", "properties": { "workingDirectory": { "type": "string" }, "pythonPath": { "type": "string" }, "timeoutSeconds": { "type": "integer" }, "contextLines": { "type": "integer" }, "compactResponse": { "type": "boolean" } }, "required": ["workingDirectory"] } },
        { "name": "run_playwright", "description": "Run Playwright and return structured failures.", "inputSchema": { "type": "object", "properties": { "workingDirectory": { "type": "string" }, "testArgs": { "type": "array", "items": { "type": "string" } }, "regressionsOnly": { "type": "boolean" }, "timeoutSeconds": { "type": "integer" }, "contextLines": { "type": "integer" }, "compactResponse": { "type": "boolean" } }, "required": ["workingDirectory"] } },
        { "name": "inspect_playwright_trace", "description": "Inspect a Playwright trace archive.", "inputSchema": { "type": "object", "properties": { "tracePath": { "type": "string" }, "workingDirectory": { "type": "string" }, "maxChars": { "type": "integer" } }, "required": ["tracePath"] } },
        { "name": "run_bun_test", "description": "Run bun test and return structured failures.", "inputSchema": { "type": "object", "properties": { "workingDirectory": { "type": "string" }, "testArgs": { "type": "array", "items": { "type": "string" } }, "regressionsOnly": { "type": "boolean" }, "timeoutSeconds": { "type": "integer" }, "contextLines": { "type": "integer" }, "compactResponse": { "type": "boolean" } }, "required": ["workingDirectory"] } },
        { "name": "run_pytest", "description": "Run pytest and return structured failures.", "inputSchema": { "type": "object", "properties": { "workingDirectory": { "type": "string" }, "testArgs": { "type": "array", "items": { "type": "string" } }, "pythonPath": { "type": "string" }, "regressionsOnly": { "type": "boolean" }, "timeoutSeconds": { "type": "integer" }, "contextLines": { "type": "integer" }, "compactResponse": { "type": "boolean" } }, "required": ["workingDirectory"] } },
        { "name": "run_vitest", "description": "Run Vitest and return structured failures.", "inputSchema": { "type": "object", "properties": { "workingDirectory": { "type": "string" }, "testArgs": { "type": "array", "items": { "type": "string" } }, "regressionsOnly": { "type": "boolean" }, "timeoutSeconds": { "type": "integer" }, "contextLines": { "type": "integer" }, "compactResponse": { "type": "boolean" } }, "required": ["workingDirectory"] } },
        { "name": "run_tsc", "description": "Run the TypeScript compiler and return diagnostics.", "inputSchema": { "type": "object", "properties": { "workingDirectory": { "type": "string" }, "tscArgs": { "type": "array", "items": { "type": "string" } }, "onlyNew": { "type": "boolean" }, "timeoutSeconds": { "type": "integer" }, "contextLines": { "type": "integer" }, "compactResponse": { "type": "boolean" } }, "required": ["workingDirectory"] } },
        { "name": "run_eslint", "description": "Run ESLint and return structured diagnostics.", "inputSchema": { "type": "object", "properties": { "workingDirectory": { "type": "string" }, "eslintArgs": { "type": "array", "items": { "type": "string" } }, "onlyNew": { "type": "boolean" }, "timeoutSeconds": { "type": "integer" }, "contextLines": { "type": "integer" }, "compactResponse": { "type": "boolean" } }, "required": ["workingDirectory"] } },
        { "name": "run_cargo_check", "description": "Run cargo check and return structured Rust compiler diagnostics.", "inputSchema": { "type": "object", "properties": { "workingDirectory": { "type": "string" }, "cargoArgs": { "type": "array", "items": { "type": "string" } }, "onlyNew": { "type": "boolean" }, "timeoutSeconds": { "type": "integer" }, "contextLines": { "type": "integer" }, "compactResponse": { "type": "boolean" } }, "required": ["workingDirectory"] } },
        { "name": "peek_file", "description": "Return a compact outline of a text file.", "inputSchema": { "type": "object", "properties": { "path": { "type": "string" }, "fullThreshold": { "type": "integer" }, "maxEntries": { "type": "integer" }, "maxLevel": { "type": "integer" } }, "required": ["path"] } },
        { "name": "read_section", "description": "Read only part of a text file.", "inputSchema": { "type": "object", "properties": { "path": { "type": "string" }, "query": { "type": "string" }, "startLine": { "type": "integer" }, "endLine": { "type": "integer" }, "maxChars": { "type": "integer" }, "ranges": { "type": "array", "items": { "oneOf": [ { "type": "string" }, { "type": "object", "properties": { "query": { "type": "string" }, "startLine": { "type": "integer" }, "endLine": { "type": "integer" } } } ] } } }, "required": ["path"] } },
        { "name": "read_symbol", "description": "Read one named declaration from a code file.", "inputSchema": { "type": "object", "properties": { "path": { "type": "string" }, "symbol": { "type": "string" }, "occurrence": { "type": "integer" }, "maxChars": { "type": "integer" } }, "required": ["path", "symbol"] } },
        { "name": "summarize_log", "description": "Summarize matching lines from a large log.", "inputSchema": { "type": "object", "properties": { "path": { "type": "string" }, "pattern": { "type": "string" }, "contextLines": { "type": "integer" }, "tailLines": { "type": "integer" }, "maxGroups": { "type": "integer" }, "maxChars": { "type": "integer" } }, "required": ["path"] } },
        { "name": "search_files", "description": "Search files under a directory and return compact matches.", "inputSchema": { "type": "object", "properties": { "path": { "type": "string" }, "pattern": { "type": "string" }, "fileName": { "type": "string" }, "caseSensitive": { "type": "boolean" }, "maxResults": { "type": "integer" }, "maxPerFile": { "type": "integer" }, "literal": { "type": "boolean" }, "contextLines": { "type": "integer" }, "exclude": { "type": "array", "items": { "type": "string" } } }, "required": ["path", "pattern"] } },
        { "name": "store_capsule", "description": "Store large evidence locally and return a compact capsule ID and summary.", "inputSchema": { "type": "object", "properties": { "kind": { "type": "string", "enum": ["file", "command-output", "test-report", "log"] }, "content": { "type": "string" }, "sourcePath": { "type": "string" }, "workingDirectory": { "type": "string" }, "label": { "type": "string" }, "maxSummaryChars": { "type": "integer" } }, "required": ["kind"] } },
        { "name": "query_capsule", "description": "Read only unseen capsule lines or an appended delta from an earlier capsule.", "inputSchema": { "type": "object", "properties": { "capsuleId": { "type": "string" }, "baseCapsuleId": { "type": "string" }, "query": { "type": "string" }, "startLine": { "type": "integer" }, "endLine": { "type": "integer" }, "onlyUnseen": { "type": "boolean" }, "maxChars": { "type": "integer" } }, "required": ["capsuleId"] } },
        { "name": "semantic_git_diff", "description": "Return compact declaration-level additions, removals, and modifications for a Git worktree.", "inputSchema": { "type": "object", "properties": { "workingDirectory": { "type": "string" }, "baseRef": { "type": "string" }, "path": { "type": "string" }, "maxChanges": { "type": "integer" }, "maxChars": { "type": "integer" } }, "required": ["workingDirectory"] } },
        { "name": "remember_context", "description": "Persist one compact fact, decision, unknown, next action, or invalidation marker.", "inputSchema": { "type": "object", "properties": { "workingDirectory": { "type": "string" }, "kind": { "type": "string", "enum": ["fact", "decision", "unknown", "next-action", "invalidated"] }, "key": { "type": "string" }, "value": { "type": "string" }, "evidence": { "type": "string" } }, "required": ["workingDirectory", "kind", "key", "value"] } },
        { "name": "query_context", "description": "Query active structured facts, decisions, unknowns, next actions, and invalidations for one workspace.", "inputSchema": { "type": "object", "properties": { "workingDirectory": { "type": "string" }, "query": { "type": "string" }, "kinds": { "type": "array", "items": { "type": "string" } }, "onlyActive": { "type": "boolean" }, "maxResults": { "type": "integer" }, "maxChars": { "type": "integer" } }, "required": ["workingDirectory"] } },
        { "name": "compile_context", "description": "Compile active workspace context into a bounded structured brief.", "inputSchema": { "type": "object", "properties": { "workingDirectory": { "type": "string" }, "goal": { "type": "string" }, "maxChars": { "type": "integer" }, "includeEvidence": { "type": "boolean" } }, "required": ["workingDirectory", "goal"] } },
        { "name": "plan_context_read", "description": "Rank the smallest next file, symbol, or section read for a question.", "inputSchema": { "type": "object", "properties": { "workingDirectory": { "type": "string" }, "question": { "type": "string" }, "paths": { "type": "array", "items": { "type": "string" } }, "maxRecommendations": { "type": "integer" }, "maxChars": { "type": "integer" } }, "required": ["workingDirectory", "question"] } },
        { "name": "prepare_handoff", "description": "Prepare a bounded AI handoff from an explicitly selected VS Code chat session.", "inputSchema": { "type": "object", "properties": { "sessionPath": { "type": "string" }, "sessionId": { "type": "string" }, "workingDirectory": { "type": "string" }, "includeTitles": { "type": "boolean" }, "nextActionHint": { "type": "string" }, "maxChars": { "type": "integer" } } } }
    ] })
}

fn visible_tool_definitions() -> Value {
    let mut definitions = tool_definitions();
    if !handoff::enabled()
        && let Some(tools) = definitions["tools"].as_array_mut()
    {
        tools.retain(|tool| !matches!(tool["name"].as_str(), Some("zuma_handoff" | "prepare_handoff")));
    }
    definitions
}

fn missing_required_arguments(name: &str, arguments: &Value) -> Option<String> {
    let definitions = tool_definitions();
    let tool = definitions["tools"]
        .as_array()?
        .iter()
        .find(|tool| tool["name"] == name)?;
    let missing: Vec<&str> = tool["inputSchema"]["required"]
        .as_array()?
        .iter()
        .filter_map(Value::as_str)
        .filter(|field| arguments.get(*field).is_none_or(Value::is_null))
        .collect();
    if missing.is_empty() {
        None
    } else {
        Some(format!(
            "Missing required argument(s) for {name}: {}.",
            missing.join(", ")
        ))
    }
}

fn uses_task_protocol(params: &Value) -> bool {
    params
        .get("_meta")
        .and_then(|meta| meta.get("io.modelcontextprotocol/protocolVersion"))
        .and_then(Value::as_str)
        .is_some_and(|version| version >= "2026-07-28")
}

fn forward_response(response: Result<Value, String>) -> Value {
    match response {
        Ok(response) if response.get("result").is_some() => response["result"].clone(),
        Ok(response) => json!({
            "content": [{ "type": "text", "text": response["error"].to_string() }],
            "isError": true
        }),
        Err(error) => json!({
            "content": [{ "type": "text", "text": error }],
            "isError": true
        }),
    }
}

fn forward_protocol_response(client_id: &Value, response: Result<Value, String>) -> Value {
    match response {
        Ok(mut response) => {
            response["id"] = client_id.clone();
            response
        }
        Err(error) => json!({
            "jsonrpc": "2.0",
            "id": client_id,
            "error": { "code": -32603, "message": error }
        }),
    }
}

fn can_handle_locally(name: &str, arguments: &Value) -> bool {
    let Some(path) = arguments.get("path").and_then(Value::as_str) else {
        return false;
    };
    let path = Path::new(path);
    match name {
        "search_files" => {
            path.is_absolute()
                && (path.is_dir() || path.is_file())
                && rust_regex_supported(arguments, "pattern", None)
        }
        "summarize_log" => {
            path.is_absolute()
                && fs::read_to_string(path).is_ok()
                && rust_regex_supported(
                    arguments,
                    "pattern",
                    Some("error|fail|exception|fatal|warn|エラー|警告|失敗|例外"),
                )
        }
        "peek_file" => {
            if fs::read_to_string(path).is_err() {
                return false;
            }
            let kind = determine_kind(path);
            let extension = path
                .extension()
                .and_then(|value| value.to_str())
                .unwrap_or("");
            kind == "markdown"
                || (kind == "code"
                    && !matches!(extension.to_ascii_lowercase().as_str(), "ps1" | "psm1"))
        }
        "read_section" => {
            fs::read_to_string(path).is_ok()
                && arguments.get("query").is_none()
                && arguments.get("ranges").is_none()
                && arguments.get("startLine").and_then(Value::as_u64).is_some()
        }
        _ => false,
    }
}

fn rust_regex_supported(arguments: &Value, name: &str, default: Option<&str>) -> bool {
    let Some(pattern) = arguments.get(name).and_then(Value::as_str).or(default) else {
        return false;
    };
    let literal = arguments
        .get("literal")
        .and_then(Value::as_bool)
        .unwrap_or(false);
    let expression = if literal {
        regex::escape(pattern)
    } else {
        pattern.to_string()
    };
    regex::RegexBuilder::new(&expression).build().is_ok()
}

fn routed_params(params: &Value) -> Option<(String, Value)> {
    let name = params.get("name")?.as_str()?;
    let arguments = params.get("arguments")?.as_object()?;
    let operation = arguments.get("operation")?.as_str()?;

    let routed_name = match name {
        "zuma_task" => match operation {
            "set" => "set_task_anchor",
            "get" => "get_task_anchor",
            "clear" => "clear_task_anchor",
            _ => return None,
        },
        "zuma_shell" => match operation {
            "execute" => "execute_powershell",
            "reset_session" => "reset_powershell_session",
            "list_sessions" => "list_powershell_sessions",
            _ => return None,
        },
        "zuma_execute" => match operation {
            "dotnet_build" => "run_dotnet_build",
            "dotnet_test" => "run_dotnet_test",
            "affected_tests" => "run_affected_tests",
            "playwright" => "run_playwright",
            "bun_test" => "run_bun_test",
            "pytest" => "run_pytest",
            "vitest" => "run_vitest",
            "tsc" => "run_tsc",
            "eslint" => "run_eslint",
            "cargo_check" => "run_cargo_check",
            _ => return None,
        },
        "zuma_read" => match operation {
            "peek_file" | "read_section" | "read_symbol" | "summarize_log" | "search_files" | "inspect_playwright_trace" => operation,
            _ => return None,
        },
        "zuma_context" => match operation {
            "store_capsule" => "store_capsule",
            "query_capsule" => "query_capsule",
            "semantic_git_diff" => "semantic_git_diff",
            "remember" => "remember_context",
            "query" => "query_context",
            "compile" => "compile_context",
            "plan_read" => "plan_context_read",
            _ => return None,
        },
        "zuma_gate" => match operation {
            "get" => "get_execution_gate",
            "set" => "set_execution_gate",
            "reset" => "reset_execution_gate",
            _ => return None,
        },
        "zuma_handoff" if operation == "prepare" => "prepare_handoff",
        _ => return None,
    };

    let mut routed = arguments.clone();
    routed.remove("operation");
    Some((routed_name.to_string(), Value::Object(routed)))
}

fn call_tool(
    client_id: &Value,
    params: &Value,
    bridge: &Bridge,
    task_anchor: Option<&Value>,
) -> Value {
    let routed = routed_params(params);
    let arguments = routed
        .as_ref()
        .map(|(_, routed_arguments)| routed_arguments)
        .or_else(|| params.get("arguments"))
        .unwrap_or(&Value::Null);
    let routed_name = routed.as_ref().map(|(name, _)| name.as_str());
    let name = match routed_name.or_else(|| params.get("name").and_then(Value::as_str)) {
        Some(name) => name,
        None => {
            return json!({ "content": [{ "type": "text", "text": "{\"status\":\"error\",\"message\":\"Tool name is required.\"}" }], "isError": true });
        }
    };

    if routed.is_some()
        && let Some(message) = missing_required_arguments(name, arguments)
    {
        return json!({ "content": [{ "type": "text", "text": json!({ "status": "error", "message": message }).to_string() }], "isError": true });
    }

    if name == "set_task_anchor" {
        return json!({ "content": [{ "type": "text", "text": bridge.task_anchors.set(arguments).to_string() }] });
    }
    if name == "get_task_anchor" {
        return json!({ "content": [{ "type": "text", "text": bridge.task_anchors.get().to_string() }] });
    }
    if name == "clear_task_anchor" {
        return json!({ "content": [{ "type": "text", "text": bridge.task_anchors.clear().to_string() }] });
    }

    if name == "prepare_handoff" {
        let payload = handoff::prepare_handoff(arguments);
        let response = payload.to_string();
        let usage_path = arguments
            .get("sessionPath")
            .and_then(Value::as_str)
            .map(Path::new)
            .unwrap_or_else(|| Path::new(""));
        record_usage(name, usage_path, &response);
        let mut result = json!({ "content": [{ "type": "text", "text": response }] });
        task_anchor::TaskAnchorStore::decorate_snapshot(&mut result, task_anchor);
        return result;
    }

    if uses_task_protocol(params) {
        let mut result =
            forward_response(bridge.task_request_for(client_id, "tools/call", params.clone()));
        task_anchor::TaskAnchorStore::decorate_snapshot(&mut result, task_anchor);
        return result;
    }

    if can_handle_locally(name, arguments) {
        let action = || match name {
            "peek_file" => peek_file(arguments),
            "read_section" => read_section(arguments),
            "search_files" => file_tools::search_files(arguments),
            "summarize_log" => file_tools::summarize_log(arguments),
            _ => unreachable!(),
        };
        let payload = if matches!(name, "peek_file" | "read_section" | "summarize_log") {
            bridge.read_guard.track(name, arguments, action)
        } else {
            action()
        };
        let response = payload.to_string();
        if let Some(path) = arguments.get("path").and_then(Value::as_str) {
            record_usage(name, Path::new(path), &response);
        }
        let mut result = json!({ "content": [{ "type": "text", "text": response }] });
        task_anchor::TaskAnchorStore::decorate_snapshot(&mut result, task_anchor);
        return result;
    }

    let forwarded_params = if let Some((routed_name, routed_arguments)) = routed.as_ref() {
        json!({
            "name": routed_name,
            "arguments": routed_arguments,
            "_meta": params.get("_meta").cloned().unwrap_or(Value::Null)
        })
    } else {
        params.clone()
    };
    let response = bridge.request_for(client_id, "tools/call", forwarded_params);
    handoff::record_verification(name, arguments, &response);
    let mut result = forward_response(response);
    task_anchor::TaskAnchorStore::decorate_snapshot(&mut result, task_anchor);
    result
}

fn handle_request(request: Request, bridge: &Bridge, task_anchor: Option<&Value>) -> Option<Value> {
    let Some(id) = request.id else {
        if request.method == "notifications/cancelled" {
            let _ = bridge.cancel(&request.params);
        } else if request.method.starts_with("notifications/") {
            let _ = bridge.notify(&request.method, request.params);
        }
        return None;
    };

    if matches!(
        request.method.as_str(),
        "tasks/get" | "tasks/update" | "tasks/cancel"
    ) {
        let mut response = forward_protocol_response(
            &id,
            bridge.task_request_for(&id, &request.method, request.params),
        );
        if let Some(result) = response.get_mut("result") {
            task_anchor::TaskAnchorStore::decorate_snapshot(result, task_anchor);
        }
        return Some(response);
    }

    let result = match request.method.as_str() {
        "initialize" => json!({
            "protocolVersion": request.params.get("protocolVersion").cloned().unwrap_or_else(|| json!("2024-11-05")),
            "capabilities": {
                "tools": { "listChanged": false },
                "extensions": { "io.modelcontextprotocol/tasks": {} }
            },
            "serverInfo": { "name": "Zuma", "version": VERSION },
            "instructions": "Zuma provides seven tools; prefer them over the generic terminal and file-read tools. zuma_shell runs PowerShell commands. zuma_execute runs builds, tests, and linters with compact results. zuma_read reads and searches files and logs with bounded output. zuma_context stores large evidence and notes outside the chat. zuma_task records the task scope: at the start of a substantial task, call zuma_task with operation set, the goal, target, acceptance criteria, out-of-scope items, and absolute allowed paths; keep it updated when scope changes and clear it only when the task is complete. zuma_gate and zuma_handoff manage loop protection and chat handoff. Every tool takes an operation argument; read its description for the valid values."
        }),
        "ping" => json!({}),
        "tools/list" => visible_tool_definitions(),
        "tools/call" => call_tool(&id, &request.params, bridge, task_anchor),
        _ => {
            return Some(json!({
                "jsonrpc": "2.0",
                "id": id,
                "error": { "code": -32601, "message": "Method not found" }
            }));
        }
    };

    Some(json!({ "jsonrpc": "2.0", "id": id, "result": result }))
}

fn run() -> io::Result<()> {
    let stdin = io::stdin();
    let stdout = Arc::new(Mutex::new(io::stdout()));
    let bridge = Arc::new(Bridge::new());
    let mut workers = Vec::new();

    for line in stdin.lock().lines() {
        let line = line?;
        if line.trim().is_empty() {
            continue;
        }

        let request: Request = match serde_json::from_str(&line) {
            Ok(request) => request,
            Err(error) => {
                let response = json!({
                    "jsonrpc": "2.0",
                    "id": null,
                    "error": { "code": -32700, "message": format!("Parse error: {error}") }
                });
                let mut output = stdout.lock().unwrap();
                writeln!(output, "{}", serde_json::to_string(&response)?)?;
                output.flush()?;
                continue;
            }
        };
        let is_anchor_call = request.method == "tools/call"
            && request
                .params
                .get("name")
                .and_then(Value::as_str)
                .is_some_and(|name| {
                    matches!(
                        name,
                        "set_task_anchor" | "get_task_anchor" | "clear_task_anchor"
                    )
                });
        if is_anchor_call {
            if let Some(response) = handle_request(request, &bridge, None) {
                let mut output = stdout.lock().unwrap();
                writeln!(output, "{response}")?;
                output.flush()?;
            }
            continue;
        }

        let anchor_tool = if request.method == "tools/call" {
            request.params.get("name").and_then(Value::as_str)
        } else if request.method.starts_with("tasks/") {
            Some(request.method.as_str())
        } else {
            None
        };
        let anchor_arguments = if request.method == "tools/call" {
            request.params.get("arguments").unwrap_or(&Value::Null)
        } else {
            &request.params
        };
        let task_anchor =
            anchor_tool.and_then(|name| bridge.task_anchors.snapshot(name, anchor_arguments));
        let worker_output = Arc::clone(&stdout);
        let worker_bridge = Arc::clone(&bridge);
        workers.push(std::thread::spawn(move || {
            if let Some(response) = handle_request(request, &worker_bridge, task_anchor.as_ref()) {
                let mut output = worker_output.lock().unwrap();
                let _ = writeln!(output, "{}", response);
                let _ = output.flush();
            }
        }));
    }

    for worker in workers {
        let _ = worker.join();
    }
    Ok(())
}

fn main() {
    let parent = match parent_pid() {
        Ok(parent) => parent,
        Err(error) => {
            eprintln!("{error}");
            std::process::exit(2);
        }
    };
    if let Err(error) = validate_pwsh_environment() {
        eprintln!("InazumaPwsh cannot start: {error}");
        std::process::exit(1);
    }
    if let Some(process_id) = parent {
        watch_parent(process_id);
    }
    if let Err(error) = run() {
        eprintln!("InazumaPwsh failed: {error}");
        std::process::exit(1);
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn routes_public_shell_tool_to_compatibility_name() {
        let params = serde_json::json!({
            "name": "zuma_shell",
            "arguments": { "operation": "list_sessions" }
        });
        let (name, arguments) = routed_params(&params).unwrap();
        assert_eq!(name, "list_powershell_sessions");
        assert_eq!(arguments, serde_json::json!({}));
    }

    #[test]
    fn tool_definitions_include_context_router() {
        let definitions = tool_definitions();
        let tools = definitions["tools"].as_array().unwrap();
        assert_eq!(tools.len(), 7);
        let context = tools
            .iter()
            .find(|tool| tool["name"] == "zuma_context")
            .expect("zuma_context router should be exposed");
        let operations = context["inputSchema"]["properties"]["operation"]["enum"]
            .as_array()
            .unwrap();
        assert!(operations.iter().any(|operation| operation == "store_capsule"));
        assert!(operations.iter().any(|operation| operation == "query_capsule"));
    }

    #[test]
    fn router_descriptions_name_every_operation() {
        let definitions = tool_definitions();
        for tool in definitions["tools"].as_array().unwrap() {
            let description = tool["description"].as_str().unwrap();
            let operations = tool["inputSchema"]["properties"]["operation"]["enum"]
                .as_array()
                .unwrap();
            for operation in operations {
                let operation = operation.as_str().unwrap();
                assert!(
                    description.contains(operation) || description.contains(&operation.replace('_', " ")),
                    "{} description omits operation {}",
                    tool["name"],
                    operation
                );
            }
        }
    }

    #[test]
    fn markdown_outline_ignores_long_fenced_content() {
        let lines: Vec<String> = [
            "# First",
            "````markdown",
            "```",
            "# Hidden",
            "````",
            "## Second",
            "~~~",
            "~~~not-a-close",
            "# Also hidden",
            "~~~",
            "## Third",
        ]
        .into_iter()
        .map(str::to_owned)
        .collect();

        let outline = markdown_outline(&lines);
        let titles: Vec<&str> = outline.iter().map(|entry| entry.title.as_str()).collect();
        let ranges: Vec<(usize, usize)> = outline
            .iter()
            .map(|entry| (entry.start, entry.end))
            .collect();
        assert_eq!(titles, ["First", "Second", "Third"]);
        assert_eq!(ranges, [(1, 11), (6, 10), (11, 11)]);
    }
}
