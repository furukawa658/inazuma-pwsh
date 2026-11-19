mod sidecar;

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
}

impl Bridge {
    fn new() -> Self {
        Self {
            sidecar: Mutex::new(None),
        }
    }

    fn sidecar(&self) -> Result<Arc<Sidecar>, String> {
        let mut sidecar = self.sidecar.lock().unwrap();
        if sidecar.is_none() {
            *sidecar = Some(Arc::new(Sidecar::start()?));
        }
        Ok(Arc::clone(sidecar.as_ref().unwrap()))
    }

    fn request_for(&self, client_id: &Value, method: &str, params: Value) -> Result<Value, String> {
        self.sidecar()?.request_for(Some(client_id), method, params)
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
    let entry = if source_bytes == 0 {
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
    if let Some(explicit) = env::var_os("INAZUMA_PWSH_PATH") {
        if !explicit.is_empty() {
            let path = PathBuf::from(explicit);
            if path.is_file() {
                return Ok(path.canonicalize().unwrap_or(path));
            }
            return Err(format!(
                "The specified INAZUMA_PWSH_PATH does not exist: {}. Fix or clear inazumaPwsh.pwshPath (INAZUMA_PWSH_PATH).",
                path.display()
            ));
        }
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
        return json!({ "status": "error", "message": "File must be an existing absolute path." });
    }

    let lines = match read_lines(path) {
        Ok(lines) => lines,
        Err(message) => return json!({ "status": "error", "message": message }),
    };
    let kind = determine_kind(path);
    let mut entries = if kind == "markdown" {
        markdown_outline(&lines)
    } else {
        text_outline(&lines)
    };
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
        "bytes": fs::metadata(path).map(|metadata| metadata.len()).unwrap_or(0),
        "recommend": if lines.len() <= integer_argument(arguments, "fullThreshold", 200).clamp(1, 5000) { "FULL" } else { "PARTIAL" },
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
        return json!({ "status": "error", "message": "File must be an existing absolute path." });
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
        { "name": "execute_powershell", "description": "Run PowerShell in a persistent session.", "inputSchema": { "type": "object", "properties": { "script": { "type": "string" }, "timeoutSeconds": { "type": "integer" }, "workingDirectory": { "type": "string" }, "maxOutputChars": { "type": "integer" }, "sessionId": { "type": "string" }, "compactResponse": { "type": "boolean" } }, "required": ["script"] } },
        { "name": "reset_powershell_session", "description": "Reset a persistent PowerShell session.", "inputSchema": { "type": "object", "properties": { "sessionId": { "type": "string" } } } },
        { "name": "list_powershell_sessions", "description": "List persistent PowerShell sessions.", "inputSchema": { "type": "object", "properties": {} } },
        { "name": "run_dotnet_build", "description": "Run dotnet build and return structured diagnostics.", "inputSchema": { "type": "object", "properties": { "workingDirectory": { "type": "string" }, "target": { "type": "string" }, "includeWarnings": { "type": "boolean" }, "timeoutSeconds": { "type": "integer" }, "contextLines": { "type": "integer" }, "compactResponse": { "type": "boolean" } }, "required": ["workingDirectory"] } },
        { "name": "run_dotnet_test", "description": "Run dotnet test and return structured failures.", "inputSchema": { "type": "object", "properties": { "workingDirectory": { "type": "string" }, "testArgs": { "type": "array", "items": { "type": "string" } }, "timeoutSeconds": { "type": "integer" }, "contextLines": { "type": "integer" }, "compactResponse": { "type": "boolean" } }, "required": ["workingDirectory"] } },
        { "name": "run_playwright", "description": "Run Playwright and return structured failures.", "inputSchema": { "type": "object", "properties": { "workingDirectory": { "type": "string" }, "testArgs": { "type": "array", "items": { "type": "string" } }, "timeoutSeconds": { "type": "integer" }, "contextLines": { "type": "integer" }, "compactResponse": { "type": "boolean" } }, "required": ["workingDirectory"] } },
        { "name": "inspect_playwright_trace", "description": "Inspect a Playwright trace archive.", "inputSchema": { "type": "object", "properties": { "tracePath": { "type": "string" }, "workingDirectory": { "type": "string" }, "maxChars": { "type": "integer" } }, "required": ["tracePath"] } },
        { "name": "run_bun_test", "description": "Run bun test and return structured failures.", "inputSchema": { "type": "object", "properties": { "workingDirectory": { "type": "string" }, "testArgs": { "type": "array", "items": { "type": "string" } }, "timeoutSeconds": { "type": "integer" }, "contextLines": { "type": "integer" }, "compactResponse": { "type": "boolean" } }, "required": ["workingDirectory"] } },
        { "name": "run_pytest", "description": "Run pytest and return structured failures.", "inputSchema": { "type": "object", "properties": { "workingDirectory": { "type": "string" }, "testArgs": { "type": "array", "items": { "type": "string" } }, "pythonPath": { "type": "string" }, "timeoutSeconds": { "type": "integer" }, "contextLines": { "type": "integer" }, "compactResponse": { "type": "boolean" } }, "required": ["workingDirectory"] } },
        { "name": "run_vitest", "description": "Run Vitest and return structured failures.", "inputSchema": { "type": "object", "properties": { "workingDirectory": { "type": "string" }, "testArgs": { "type": "array", "items": { "type": "string" } }, "timeoutSeconds": { "type": "integer" }, "contextLines": { "type": "integer" }, "compactResponse": { "type": "boolean" } }, "required": ["workingDirectory"] } },
        { "name": "run_tsc", "description": "Run the TypeScript compiler and return diagnostics.", "inputSchema": { "type": "object", "properties": { "workingDirectory": { "type": "string" }, "tscArgs": { "type": "array", "items": { "type": "string" } }, "onlyNew": { "type": "boolean" }, "timeoutSeconds": { "type": "integer" }, "contextLines": { "type": "integer" }, "compactResponse": { "type": "boolean" } }, "required": ["workingDirectory"] } },
        { "name": "run_eslint", "description": "Run ESLint and return structured diagnostics.", "inputSchema": { "type": "object", "properties": { "workingDirectory": { "type": "string" }, "eslintArgs": { "type": "array", "items": { "type": "string" } }, "timeoutSeconds": { "type": "integer" }, "contextLines": { "type": "integer" }, "compactResponse": { "type": "boolean" } }, "required": ["workingDirectory"] } },
        { "name": "peek_file", "description": "Return a compact outline of a text file.", "inputSchema": { "type": "object", "properties": { "path": { "type": "string" }, "fullThreshold": { "type": "integer" }, "maxEntries": { "type": "integer" }, "maxLevel": { "type": "integer" } }, "required": ["path"] } },
        { "name": "read_section", "description": "Read only part of a text file.", "inputSchema": { "type": "object", "properties": { "path": { "type": "string" }, "query": { "type": "string" }, "startLine": { "type": "integer" }, "endLine": { "type": "integer" }, "maxChars": { "type": "integer" }, "ranges": { "type": "array" } }, "required": ["path"] } },
        { "name": "summarize_log", "description": "Summarize matching lines from a large log.", "inputSchema": { "type": "object", "properties": { "path": { "type": "string" }, "pattern": { "type": "string" }, "contextLines": { "type": "integer" }, "tailLines": { "type": "integer" }, "maxGroups": { "type": "integer" }, "maxChars": { "type": "integer" } }, "required": ["path"] } },
        { "name": "search_files", "description": "Search files under a directory and return compact matches.", "inputSchema": { "type": "object", "properties": { "path": { "type": "string" }, "pattern": { "type": "string" }, "fileName": { "type": "string" }, "caseSensitive": { "type": "boolean" }, "maxResults": { "type": "integer" }, "maxPerFile": { "type": "integer" }, "literal": { "type": "boolean" }, "contextLines": { "type": "integer" }, "exclude": { "type": "array", "items": { "type": "string" } } }, "required": ["path", "pattern"] } }
    ] })
}

fn can_handle_locally(name: &str, arguments: &Value) -> bool {
    let Some(path) = arguments.get("path").and_then(Value::as_str) else {
        return false;
    };
    let path = Path::new(path);
    if fs::read_to_string(path).is_err() {
        return false;
    }
    match name {
        "peek_file" => determine_kind(path) == "markdown",
        "read_section" => {
            arguments.get("query").is_none()
                && arguments.get("ranges").is_none()
                && arguments.get("startLine").and_then(Value::as_u64).is_some()
        }
        _ => false,
    }
}

fn call_tool(client_id: &Value, params: &Value, bridge: &Bridge) -> Value {
    let arguments = params.get("arguments").unwrap_or(&Value::Null);
    let name = match params.get("name").and_then(Value::as_str) {
        Some(name) => name,
        None => {
            return json!({ "content": [{ "type": "text", "text": "{\"status\":\"error\",\"message\":\"Tool name is required.\"}" }], "isError": true });
        }
    };

    if can_handle_locally(name, arguments) {
        let payload = if name == "peek_file" {
            peek_file(arguments)
        } else {
            read_section(arguments)
        };
        let response = payload.to_string();
        if let Some(path) = arguments.get("path").and_then(Value::as_str) {
            record_usage(name, Path::new(path), &response);
        }
        return json!({ "content": [{ "type": "text", "text": response }] });
    }

    match bridge.request_for(client_id, "tools/call", params.clone()) {
        Ok(response) if response.get("result").is_some() => response["result"].clone(),
        Ok(response) => {
            json!({ "content": [{ "type": "text", "text": response["error"].to_string() }], "isError": true })
        }
        Err(error) => json!({ "content": [{ "type": "text", "text": error }], "isError": true }),
    }
}

fn handle_request(request: Request, bridge: &Bridge) -> Option<Value> {
    let Some(id) = request.id else {
        if request.method == "notifications/cancelled" {
            let _ = bridge.cancel(&request.params);
        } else if request.method.starts_with("notifications/") {
            let _ = bridge.notify(&request.method, request.params);
        }
        return None;
    };

    let result = match request.method.as_str() {
        "initialize" => json!({
            "protocolVersion": request.params.get("protocolVersion").cloned().unwrap_or_else(|| json!("2024-11-05")),
            "capabilities": { "tools": { "listChanged": false } },
            "serverInfo": { "name": "Zuma", "version": VERSION }
        }),
        "ping" => json!({}),
        "tools/list" => tool_definitions(),
        "tools/call" => call_tool(&id, &request.params, bridge),
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

        let worker_output = Arc::clone(&stdout);
        let worker_bridge = Arc::clone(&bridge);
        workers.push(std::thread::spawn(move || {
            if let Some(response) = handle_request(request, &worker_bridge) {
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
