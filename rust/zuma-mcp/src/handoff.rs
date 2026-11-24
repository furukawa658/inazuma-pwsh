use serde_json::{Value, json};
use std::env;
use std::fs::{self, File};
use std::io::{BufRead, BufReader};
use std::path::{Path, PathBuf};
use std::process::Command;

const DEFAULT_MAX_CHARS: usize = 6000;
const MAX_INPUT_BYTES: u64 = 50 * 1024 * 1024;

fn enabled() -> bool {
    env::var("ZUMA_HANDOFF_ENABLED")
        .ok()
        .is_some_and(|value| value.eq_ignore_ascii_case("true"))
}

fn error(message: impl Into<String>) -> Value {
    json!({ "status": "error", "message": message.into() })
}

fn string_argument<'a>(arguments: &'a Value, name: &str) -> Option<&'a str> {
    arguments.get(name).and_then(Value::as_str)
}

fn integer_argument(arguments: &Value, name: &str, default: usize) -> usize {
    arguments
        .get(name)
        .and_then(Value::as_u64)
        .map(|value| value as usize)
        .unwrap_or(default)
}

fn set_at_path(root: &mut Value, path: &[Value], replacement: Value) -> Result<(), String> {
    if path.is_empty() {
        *root = replacement;
        return Ok(());
    }
    let mut current = root;
    for (index, segment) in path.iter().enumerate() {
        let last = index == path.len() - 1;
        if let Some(key) = segment.as_str() {
            let object = current
                .as_object_mut()
                .ok_or_else(|| format!("Cannot apply chat delta through object key {key}"))?;
            if last {
                object.insert(key.to_string(), replacement);
                return Ok(());
            }
            current = object
                .get_mut(key)
                .ok_or_else(|| format!("Chat delta path does not exist: {key}"))?;
        } else if let Some(array_index) = segment.as_u64().map(|value| value as usize) {
            let array = current.as_array_mut().ok_or_else(|| {
                format!("Cannot apply chat delta through array index {array_index}")
            })?;
            if last {
                if array_index >= array.len() {
                    return Err(format!(
                        "Chat delta array index {array_index} is out of range"
                    ));
                }
                array[array_index] = replacement;
                return Ok(());
            }
            current = array
                .get_mut(array_index)
                .ok_or_else(|| format!("Chat delta path does not exist: {array_index}"))?;
        } else {
            return Err("Chat delta path contains an unsupported segment".to_string());
        }
    }
    Ok(())
}

fn append_at_path(root: &mut Value, path: &[Value], additions: Value) -> Result<(), String> {
    let mut current = root;
    for segment in path {
        if let Some(key) = segment.as_str() {
            current = current
                .as_object_mut()
                .ok_or_else(|| format!("Cannot append through object key {key}"))?
                .get_mut(key)
                .ok_or_else(|| format!("Chat append path does not exist: {key}"))?;
        } else if let Some(array_index) = segment.as_u64().map(|value| value as usize) {
            current = current
                .as_array_mut()
                .ok_or_else(|| format!("Cannot append through array index {array_index}"))?
                .get_mut(array_index)
                .ok_or_else(|| format!("Chat append path does not exist: {array_index}"))?;
        } else {
            return Err("Chat append path contains an unsupported segment".to_string());
        }
    }
    let target = current
        .as_array_mut()
        .ok_or("Chat append target is not an array")?;
    let items = additions
        .as_array()
        .ok_or("Chat append value is not an array")?;
    target.extend(items.iter().cloned());
    Ok(())
}

fn replay_session(path: &Path) -> Result<(Value, u64), String> {
    let metadata = fs::metadata(path).map_err(|error| error.to_string())?;
    if !metadata.is_file() {
        return Err("sessionPath must be an existing file".to_string());
    }
    if metadata.len() > MAX_INPUT_BYTES {
        return Err("Chat session exceeds 50 MB limit".to_string());
    }

    let file = File::open(path).map_err(|error| error.to_string())?;
    let reader = BufReader::new(file);
    let mut state = None;
    for (index, line) in reader.lines().enumerate() {
        let line_number = index + 1;
        let line = line
            .map_err(|error| format!("Could not read chat session line {line_number}: {error}"))?;
        if line.trim().is_empty() {
            continue;
        }
        let event: Value = serde_json::from_str(&line)
            .map_err(|error| format!("Invalid chat session JSON at line {line_number}: {error}"))?;
        let kind = event.get("kind").and_then(Value::as_u64);
        match kind {
            Some(0) if state.is_none() => {
                state = event.get("v").cloned();
            }
            Some(1) => {
                let Some(root) = state.as_mut() else {
                    return Err("Chat delta appeared before the initial state".to_string());
                };
                let path = event
                    .get("k")
                    .and_then(Value::as_array)
                    .ok_or_else(|| format!("Chat replacement at line {line_number} has no path"))?;
                let value = event.get("v").cloned().ok_or_else(|| {
                    format!("Chat replacement at line {line_number} has no value")
                })?;
                set_at_path(root, path, value).map_err(|error| {
                    format!("Invalid chat replacement at line {line_number}: {error}")
                })?;
            }
            Some(2) => {
                let Some(root) = state.as_mut() else {
                    return Err("Chat delta appeared before the initial state".to_string());
                };
                let path = event
                    .get("k")
                    .and_then(Value::as_array)
                    .ok_or_else(|| format!("Chat append at line {line_number} has no path"))?;
                let value = event
                    .get("v")
                    .cloned()
                    .ok_or_else(|| format!("Chat append at line {line_number} has no value"))?;
                append_at_path(root, path, value).map_err(|error| {
                    format!("Invalid chat append at line {line_number}: {error}")
                })?;
            }
            Some(0) => return Err("Chat session contains more than one initial state".to_string()),
            _ => return Err(format!("Unsupported chat event kind at line {line_number}")),
        }
    }
    Ok((
        state.ok_or("Chat session contains no initial state")?,
        metadata.len(),
    ))
}

fn secret_patterns() -> [&'static str; 4] {
    [
        r"(?i)bearer\s+[A-Za-z0-9._~+/=-]+",
        r"(?i)api[_-]?key\s*[:=]\s*[^\s,;]+",
        r"(?i)(?:password|passwd|secret)\s*[:=]\s*[^\s,;]*",
        r"(?i)(?:https?://[^\s?]+)\?[^\s]+",
    ]
}

fn redact(text: &str) -> String {
    let mut output = text.to_string();
    for pattern in secret_patterns() {
        let regex = regex::Regex::new(pattern).unwrap();
        output = regex.replace_all(&output, "[REDACTED]").to_string();
    }
    output
}

fn bounded_text(text: String, max_chars: usize) -> String {
    if text.chars().count() <= max_chars {
        return text;
    }
    if max_chars <= 3 {
        return text.chars().take(max_chars).collect();
    }
    format!(
        "{}...",
        text.chars().take(max_chars - 3).collect::<String>()
    )
}

fn request_texts(state: &Value) -> Vec<String> {
    state
        .get("requests")
        .and_then(Value::as_array)
        .into_iter()
        .flatten()
        .filter_map(|request| request.get("message"))
        .filter_map(|message| message.get("text"))
        .filter_map(Value::as_str)
        .map(redact)
        .map(|text| bounded_text(text, 1000))
        .filter(|text| !text.trim().is_empty())
        .collect()
}

fn git_summary(working_directory: &str) -> Vec<String> {
    if !Path::new(working_directory).is_absolute() {
        return Vec::new();
    }
    let status = Command::new("git")
        .arg("-C")
        .arg(working_directory)
        .args(["status", "--short"])
        .output();
    let Ok(status) = status else {
        return Vec::new();
    };
    if !status.status.success() {
        return Vec::new();
    }
    String::from_utf8_lossy(&status.stdout)
        .lines()
        .filter(|line| !line.trim().is_empty())
        .take(50)
        .map(|line| bounded_text(redact(line), 300))
        .collect()
}

fn push_section(lines: &mut Vec<String>, title: &str, values: impl IntoIterator<Item = String>) {
    let values: Vec<String> = values
        .into_iter()
        .filter(|value| !value.is_empty())
        .collect();
    if values.is_empty() {
        return;
    }
    if !lines.is_empty() {
        lines.push(String::new());
    }
    lines.push(title.to_string());
    lines.extend(values.into_iter().map(|value| format!("- {value}")));
}

fn last_verification(working_directory: &str) -> Option<String> {
    let path = env::temp_dir()
        .join("inazuma-pwsh-logs")
        .join("verification-history.jsonl");
    let file = File::open(path).ok()?;
    let mut latest = None;
    for line in BufReader::new(file).lines().map_while(Result::ok) {
        let Ok(entry) = serde_json::from_str::<Value>(&line) else {
            continue;
        };
        let workspace = entry.get("workspace").and_then(Value::as_str).unwrap_or("");
        let same_workspace = if cfg!(windows) {
            workspace.eq_ignore_ascii_case(working_directory)
        } else {
            workspace == working_directory
        };
        if same_workspace {
            latest = Some(entry);
        }
    }
    let latest = latest?;
    let tool = latest
        .get("tool")
        .and_then(Value::as_str)
        .unwrap_or("unknown");
    let status = latest
        .get("status")
        .and_then(Value::as_str)
        .unwrap_or("unknown");
    let failures = latest
        .get("failureCount")
        .and_then(Value::as_i64)
        .unwrap_or(0);
    let detail = latest
        .get("firstFailure")
        .and_then(|failure| {
            let file = failure.get("file").and_then(Value::as_str).unwrap_or("");
            let line = failure
                .get("line")
                .and_then(Value::as_u64)
                .map(|line| format!(":{line}"))
                .unwrap_or_default();
            let message = failure.get("message").and_then(Value::as_str).unwrap_or("");
            if file.is_empty() && message.is_empty() {
                None
            } else {
                Some(format!("; firstFailure: {file}{line} {message}"))
            }
        })
        .unwrap_or_default();
    Some(format!("{tool}: {status}; failures: {failures}{detail}"))
}

fn build_handoff(
    state: &Value,
    working_directory: &str,
    next_action_hint: Option<&str>,
    max_chars: usize,
) -> String {
    let requests = request_texts(state);
    let goal = requests.first().cloned();
    let decisions: Vec<String> = requests.iter().skip(1).rev().take(3).cloned().collect();
    let changed = git_summary(working_directory)
        .into_iter()
        .map(|line| format!("changed: {line}"))
        .collect::<Vec<_>>();
    let verification = last_verification(working_directory);
    let next_action = next_action_hint
        .map(redact)
        .map(|text| bounded_text(text, 1000))
        .filter(|text| !text.trim().is_empty())
        .unwrap_or_else(|| "notAvailable: provide nextActionHint".to_string());

    let mut sections = vec![
        ("GOAL", goal.into_iter().collect::<Vec<_>>()),
        ("RECENT_INSTRUCTIONS", decisions),
        ("WORKSPACE", changed),
        (
            "LAST_VERIFICATION",
            verification.into_iter().collect::<Vec<_>>(),
        ),
        ("NEXT_ACTION", vec![next_action]),
    ];
    let render = |items: &[(&str, Vec<String>)]| {
        let mut lines = Vec::new();
        for (title, values) in items {
            push_section(&mut lines, title, values.iter().cloned());
        }
        lines.join("\n")
    };
    let mut text = render(&sections);
    if text.chars().count() > max_chars {
        sections.retain(|(title, _)| *title != "RECENT_INSTRUCTIONS");
        text = render(&sections);
    }
    if text.chars().count() <= max_chars {
        return text;
    }

    sections.retain(|(title, _)| {
        *title == "GOAL" || *title == "LAST_VERIFICATION" || *title == "NEXT_ACTION"
    });
    let value_for = |name: &str| {
        sections
            .iter()
            .find(|(title, _)| *title == name)
            .and_then(|(_, values)| values.first())
            .cloned()
            .unwrap_or_default()
    };
    let goal_value = bounded_text(value_for("GOAL"), 250);
    let verification_value = bounded_text(value_for("LAST_VERIFICATION"), 350);
    let next_value = value_for("NEXT_ACTION");
    let mut essential = Vec::new();
    if !goal_value.is_empty() {
        essential.push(format!("GOAL\n- {goal_value}"));
    }
    if !verification_value.is_empty() {
        essential.push(format!("LAST_VERIFICATION\n- {verification_value}"));
    }
    let fixed = essential.join("\n\n");
    let overhead = fixed.chars().count()
        + if fixed.is_empty() { 0 } else { 2 }
        + "NEXT_ACTION\n- ".chars().count();
    let available = max_chars.saturating_sub(overhead);
    essential.push(format!(
        "NEXT_ACTION\n- {}",
        bounded_text(next_value, available)
    ));
    essential.join("\n\n")
}

fn vscode_storage_roots() -> Vec<PathBuf> {
    if let Some(root) = env::var_os("ZUMA_CHAT_STORAGE_ROOT") {
        return vec![PathBuf::from(root)];
    }
    if cfg!(windows) {
        return env::var_os("APPDATA")
            .map(|app_data| vec![PathBuf::from(app_data).join("Code/User/workspaceStorage")])
            .unwrap_or_default();
    }
    if cfg!(target_os = "macos") {
        return env::var_os("HOME")
            .map(|home| {
                vec![
                    PathBuf::from(home)
                        .join("Library/Application Support/Code/User/workspaceStorage"),
                ]
            })
            .unwrap_or_default();
    }
    env::var_os("HOME")
        .map(|home| vec![PathBuf::from(home).join(".config/Code/User/workspaceStorage")])
        .unwrap_or_default()
}

fn candidate_from_path(path: &Path, title: bool) -> Option<Value> {
    let metadata = fs::metadata(path).ok()?;
    if !metadata.is_file() || metadata.len() > MAX_INPUT_BYTES {
        return None;
    }
    let mut candidate = json!({
        "sessionId": path.file_stem().and_then(|value| value.to_str()).unwrap_or(""),
        "path": path,
        "bytes": metadata.len(),
        "modified": metadata.modified().ok().map(|time| time.duration_since(std::time::UNIX_EPOCH).ok().map(|duration| duration.as_millis())).flatten()
    });
    if title {
        if let Ok((state, _)) = replay_session(path) {
            if let Some(custom_title) = state.get("customTitle").and_then(Value::as_str) {
                candidate["title"] = Value::String(redact(custom_title));
            }
        }
    }
    Some(candidate)
}

fn find_candidates(session_id: Option<&str>, include_titles: bool) -> Vec<Value> {
    let mut candidates = Vec::new();
    for storage in vscode_storage_roots() {
        let Ok(workspaces) = fs::read_dir(storage) else {
            continue;
        };
        for workspace in workspaces.flatten() {
            let chat_sessions = workspace.path().join("chatSessions");
            let Ok(sessions) = fs::read_dir(chat_sessions) else {
                continue;
            };
            for session in sessions.flatten() {
                let path = session.path();
                if path.extension().and_then(|value| value.to_str()) != Some("jsonl") {
                    continue;
                }
                if let Some(session_id) = session_id {
                    if path.file_stem().and_then(|value| value.to_str()) != Some(session_id) {
                        continue;
                    }
                }
                if let Some(candidate) = candidate_from_path(&path, include_titles) {
                    candidates.push(candidate);
                }
            }
        }
    }
    candidates.sort_by(|left, right| right["modified"].as_u64().cmp(&left["modified"].as_u64()));
    candidates.into_iter().take(50).collect()
}

pub fn prepare_handoff(arguments: &Value) -> Value {
    if !enabled() {
        return error(
            "Chat handoff is disabled. Enable inazumaPwsh.handoff.enabled before calling prepare_handoff.",
        );
    }

    let include_titles = arguments
        .get("includeTitles")
        .and_then(Value::as_bool)
        .unwrap_or(false);
    let session_id = string_argument(arguments, "sessionId");
    let session_path = string_argument(arguments, "sessionPath").map(PathBuf::from);
    let path = if let Some(path) = session_path {
        path
    } else if session_id.is_some() {
        let candidates = find_candidates(session_id, include_titles);
        if candidates.len() != 1 {
            return json!({ "status": "candidates", "candidates": candidates, "selectionRequired": true });
        }
        PathBuf::from(candidates[0]["path"].as_str().unwrap_or_default())
    } else {
        return json!({
            "status": "candidates",
            "candidates": find_candidates(None, include_titles),
            "selectionRequired": true
        });
    };

    if !path.is_absolute() {
        return error("sessionPath must be absolute");
    }
    if path.extension().and_then(|value| value.to_str()) != Some("jsonl") {
        return error("sessionPath must point to a .jsonl file");
    }
    let (state, source_bytes) = match replay_session(&path) {
        Ok(result) => result,
        Err(message) => return error(message),
    };
    let working_directory = string_argument(arguments, "workingDirectory")
        .or_else(|| state.get("initialLocation").and_then(Value::as_str))
        .unwrap_or("");
    let max_chars = integer_argument(arguments, "maxChars", DEFAULT_MAX_CHARS).clamp(1000, 12000);
    let handoff_text = build_handoff(
        &state,
        working_directory,
        string_argument(arguments, "nextActionHint"),
        max_chars,
    );
    let response_bytes = handoff_text.len();
    json!({
        "status": "success",
        "sessionPath": path,
        "sourceBytes": source_bytes,
        "responseBytes": response_bytes,
        "handoffText": handoff_text
    })
}
