use serde::{Deserialize, Serialize};
use serde_json::{Value, json};
use std::path::{Component, Path, PathBuf};
use std::sync::Mutex;

const DEFAULT_SESSION_ID: &str = "default";
const MAX_TEXT_LENGTH: usize = 2_000;
const MAX_LIST_ITEMS: usize = 32;
const MAX_BASE_REMINDER_BYTES: usize = 5_500;
const MAX_REMINDER_BYTES: usize = 6_000;
const MAX_WARNING_CHARS: usize = 300;

#[derive(Clone, Debug, Deserialize, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct TaskAnchor {
    goal: String,
    target: String,
    #[serde(default)]
    acceptance_criteria: Vec<String>,
    #[serde(default)]
    out_of_scope: Vec<String>,
    #[serde(default)]
    allowed_paths: Vec<String>,
    #[serde(default = "default_session_id")]
    session_id: String,
}

fn default_session_id() -> String {
    DEFAULT_SESSION_ID.to_string()
}

pub struct TaskAnchorStore {
    active: Mutex<Option<TaskAnchor>>,
}

impl TaskAnchorStore {
    pub fn new() -> Self {
        Self {
            active: Mutex::new(None),
        }
    }

    pub fn set(&self, arguments: &Value) -> Value {
        match parse_anchor(arguments) {
            Ok(anchor) => {
                let reminder = anchor.reminder();
                *self.active.lock().unwrap() = Some(anchor);
                json!({ "status": "set", "taskAnchor": reminder })
            }
            Err(message) => json!({ "status": "error", "message": message }),
        }
    }

    pub fn get(&self) -> Value {
        let anchor = self.active.lock().unwrap();
        match anchor.as_ref() {
            Some(anchor) => json!({ "status": "active", "taskAnchor": anchor.reminder() }),
            None => json!({ "status": "none" }),
        }
    }

    pub fn clear(&self) -> Value {
        *self.active.lock().unwrap() = None;
        json!({ "status": "cleared" })
    }

    pub fn snapshot(&self, tool: &str, arguments: &Value) -> Option<Value> {
        let anchor = self.active.lock().unwrap();
        let anchor = anchor.as_ref()?;
        let mut reminder = anchor.reminder();
        let warnings = anchor.scope_warnings(tool, arguments);
        if !warnings.is_empty() {
            let mut bounded = Vec::new();
            bounded.push(truncate(&warnings[0], MAX_WARNING_CHARS));
            if warnings.len() > 1 {
                bounded.push(format!(
                    "{} additional paths are outside allowedPaths.",
                    warnings.len() - 1
                ));
            }
            reminder["scopeWarnings"] = json!(bounded);
            if serialized_size(&reminder) > MAX_REMINDER_BYTES {
                reminder["scopeWarnings"] = json!([
                    "Scope warning details were omitted because the reminder reached its size limit."
                ]);
            }
        }
        Some(reminder)
    }

    pub fn decorate_snapshot(result: &mut Value, reminder: Option<&Value>) {
        let Some(reminder) = reminder else {
            return;
        };
        let added_to_content = result.get("content").and_then(Value::as_array).is_some()
            && add_json_field(result, "taskAnchor", reminder.clone());
        if !added_to_content && let Some(object) = result.as_object_mut() {
            object.insert("taskAnchor".to_string(), reminder.clone());
        }
    }

    #[cfg(test)]
    pub fn decorate(&self, tool: &str, arguments: &Value, result: &mut Value) {
        let reminder = self.snapshot(tool, arguments);
        Self::decorate_snapshot(result, reminder.as_ref());
    }
}

impl TaskAnchor {
    fn reminder(&self) -> Value {
        json!({
            "goal": self.goal,
            "target": self.target,
            "acceptanceCriteria": self.acceptance_criteria,
            "outOfScope": self.out_of_scope,
            "allowedPaths": self.allowed_paths,
            "sessionId": self.session_id,
            "instruction": "Keep the next action inside this target and scope. Recheck the acceptance criteria before completion."
        })
    }

    fn scope_warnings(&self, tool: &str, arguments: &Value) -> Vec<String> {
        if self.allowed_paths.is_empty() {
            return Vec::new();
        }
        let mut paths = Vec::new();
        for name in ["path", "tracePath", "workingDirectory", "pythonPath"] {
            if let Some(path) = arguments.get(name).and_then(Value::as_str) {
                paths.push(path.to_string());
            }
        }
        if tool == "execute_powershell"
            && arguments.get("workingDirectory").is_none()
            && self.session_id == DEFAULT_SESSION_ID
        {
            paths.push(std::env::var("ZUMA_WORKSPACE_ROOT").unwrap_or_else(|_| ".".to_string()));
        }

        paths
            .into_iter()
            .filter(|path| !is_within_any(Path::new(path), &self.allowed_paths))
            .map(|path| format!("Path is outside allowedPaths: {path}"))
            .collect()
    }
}

fn parse_anchor(arguments: &Value) -> Result<TaskAnchor, String> {
    let goal = required_text(arguments, "goal")?;
    let target = required_text(arguments, "target")?;
    let acceptance_criteria = string_list(arguments, "acceptanceCriteria")?;
    let out_of_scope = string_list(arguments, "outOfScope")?;
    let allowed_paths = string_list(arguments, "allowedPaths")?;
    for path in &allowed_paths {
        if !Path::new(path).is_absolute() {
            return Err(format!("allowedPaths entry must be absolute: {path}"));
        }
    }
    let session_id = arguments
        .get("sessionId")
        .and_then(Value::as_str)
        .unwrap_or(DEFAULT_SESSION_ID);
    validate_length("sessionId", session_id, 64)?;
    let anchor = TaskAnchor {
        goal,
        target,
        acceptance_criteria,
        out_of_scope,
        allowed_paths,
        session_id: session_id.to_string(),
    };
    if serialized_size(&anchor.reminder()) > MAX_BASE_REMINDER_BYTES {
        return Err(format!(
            "taskAnchor must fit within {MAX_BASE_REMINDER_BYTES} UTF-8 bytes before scope warnings"
        ));
    }
    Ok(anchor)
}

fn required_text(arguments: &Value, name: &str) -> Result<String, String> {
    let value = arguments
        .get(name)
        .and_then(Value::as_str)
        .ok_or_else(|| format!("{name} is required"))?;
    validate_length(name, value, MAX_TEXT_LENGTH)?;
    Ok(value.to_string())
}

fn string_list(arguments: &Value, name: &str) -> Result<Vec<String>, String> {
    let Some(array) = arguments.get(name).and_then(Value::as_array) else {
        return Ok(Vec::new());
    };
    if array.len() > MAX_LIST_ITEMS {
        return Err(format!("{name} may contain at most {MAX_LIST_ITEMS} items"));
    }
    array
        .iter()
        .map(|value| {
            let text = value
                .as_str()
                .ok_or_else(|| format!("{name} items must be strings"))?;
            validate_length(name, text, MAX_TEXT_LENGTH)?;
            Ok(text.to_string())
        })
        .collect()
}

fn validate_length(name: &str, value: &str, maximum: usize) -> Result<(), String> {
    if value.trim().is_empty() || value.chars().count() > maximum {
        return Err(format!("{name} must contain 1 to {maximum} characters"));
    }
    Ok(())
}

fn serialized_size(value: &Value) -> usize {
    serde_json::to_vec(value)
        .map(|bytes| bytes.len())
        .unwrap_or(usize::MAX)
}

fn truncate(value: &str, maximum: usize) -> String {
    let mut shortened = value.chars().take(maximum).collect::<String>();
    if value.chars().count() > maximum {
        shortened.push_str("...");
    }
    shortened
}

fn is_within_any(path: &Path, allowed_paths: &[String]) -> bool {
    let path = normalize_path(path);
    allowed_paths.iter().any(|allowed| {
        let allowed = normalize_path(Path::new(allowed));
        if cfg!(windows) {
            path.to_string_lossy()
                .to_ascii_lowercase()
                .starts_with(&format!(
                    "{}\\",
                    allowed
                        .to_string_lossy()
                        .trim_end_matches('\\')
                        .to_ascii_lowercase()
                ))
                || path
                    .to_string_lossy()
                    .eq_ignore_ascii_case(&allowed.to_string_lossy())
        } else {
            path.starts_with(allowed)
        }
    })
}

fn normalize_path(path: &Path) -> PathBuf {
    let source = path.canonicalize().unwrap_or_else(|_| path.to_path_buf());
    let mut normalized = PathBuf::new();
    for component in source.components() {
        match component {
            Component::CurDir => {}
            Component::ParentDir => {
                normalized.pop();
            }
            Component::Prefix(_) | Component::RootDir | Component::Normal(_) => {
                normalized.push(component.as_os_str());
            }
        }
    }
    normalized
}

fn add_json_field(result: &mut Value, name: &str, value: Value) -> bool {
    let Some(content) = result.get_mut("content").and_then(Value::as_array_mut) else {
        return false;
    };
    let Some(index) = content
        .iter()
        .position(|item| item.get("text").and_then(Value::as_str).is_some())
    else {
        return false;
    };
    let Some(text) = content[index].get("text").and_then(Value::as_str) else {
        return false;
    };
    let Ok(mut payload) = serde_json::from_str::<Value>(text) else {
        return false;
    };
    let Some(object) = payload.as_object_mut() else {
        return false;
    };
    object.insert(name.to_string(), value);
    content[index]["text"] = json!(payload.to_string());
    true
}

#[cfg(test)]
mod tests {
    use super::*;

    fn anchor() -> Value {
        json!({
            "goal": "Keep the deposit work in scope",
            "target": "Issue 114",
            "acceptanceCriteria": ["Tests pass"],
            "outOfScope": ["Equity"],
            "allowedPaths": [r"C:\Repo\Allowed"],
            "sessionId": "chat-1"
        })
    }

    #[test]
    fn set_get_and_clear_anchor() {
        let store = TaskAnchorStore::new();
        assert_eq!(store.set(&anchor())["status"], "set");
        assert_eq!(store.get()["taskAnchor"]["target"], "Issue 114");
        assert_eq!(store.clear()["status"], "cleared");
        assert_eq!(store.get()["status"], "none");
    }

    #[test]
    fn decorates_payload_and_warns_for_outside_path() {
        let store = TaskAnchorStore::new();
        store.set(&anchor());
        let arguments = json!({ "path": r"C:\Repo\Other\file.cs" });
        let mut result = json!({
            "content": [{ "type": "text", "text": "{\"status\":\"success\"}" }]
        });
        store.decorate("peek_file", &arguments, &mut result);
        let payload: Value =
            serde_json::from_str(result["content"][0]["text"].as_str().unwrap()).unwrap();
        assert_eq!(payload["taskAnchor"]["target"], "Issue 114");
        assert_eq!(
            payload["taskAnchor"]["scopeWarnings"]
                .as_array()
                .unwrap()
                .len(),
            1
        );
    }

    #[test]
    fn rejects_relative_allowed_path() {
        let store = TaskAnchorStore::new();
        let mut value = anchor();
        value["allowedPaths"] = json!(["relative/path"]);
        assert_eq!(store.set(&value)["status"], "error");
    }

    #[test]
    fn normalizes_parent_components_for_nonexistent_paths() {
        assert!(is_within_any(
            Path::new(r"C:\Repo\Allowed\missing\..\file.cs"),
            &[r"C:\Repo\Allowed".to_string()]
        ));
        assert!(!is_within_any(
            Path::new(r"C:\Repo\Allowed\missing\..\..\Other\file.cs"),
            &[r"C:\Repo\Allowed".to_string()]
        ));
    }

    #[test]
    fn rejects_reminders_larger_than_the_response_budget() {
        let store = TaskAnchorStore::new();
        let mut value = anchor();
        value["acceptanceCriteria"] = json!(vec!["x".repeat(2_000); 32]);
        assert_eq!(store.set(&value)["status"], "error");
    }

    #[test]
    fn keeps_scope_warning_decoration_within_the_response_budget() {
        let store = TaskAnchorStore::new();
        let mut value = anchor();
        value["acceptanceCriteria"] = json!(["x".repeat(2_000), "x".repeat(2_000)]);
        assert_eq!(store.set(&value)["status"], "set");
        let reminder = store
            .snapshot(
                "peek_file",
                &json!({ "path": format!(r"C:\Other\{}", "x".repeat(2_000)) }),
            )
            .unwrap();
        assert!(reminder["scopeWarnings"].is_array());
        assert!(serialized_size(&reminder) <= MAX_REMINDER_BYTES);
    }

    #[test]
    fn falls_back_to_top_level_anchor_for_non_json_content() {
        let store = TaskAnchorStore::new();
        store.set(&anchor());
        let mut result = json!({
            "content": [{ "type": "text", "text": "plain text" }]
        });
        store.decorate("peek_file", &json!({}), &mut result);
        assert_eq!(result["taskAnchor"]["target"], "Issue 114");
    }
}
