use serde_json::{Value, json};
use std::collections::HashMap;
use std::collections::hash_map::DefaultHasher;
use std::fs;
use std::hash::{Hash, Hasher};
use std::sync::Mutex;
use std::time::{Duration, SystemTime, UNIX_EPOCH};

const TTL: Duration = Duration::from_secs(5 * 60);
const MAX_ENTRIES: usize = 2000;

#[derive(Clone)]
struct Entry {
    returned_at: SystemTime,
    response_chars: usize,
}

pub(crate) struct RedundantReadGuard {
    entries: Mutex<HashMap<String, Entry>>,
}

impl RedundantReadGuard {
    pub(crate) fn new() -> Self {
        Self {
            entries: Mutex::new(HashMap::new()),
        }
    }

    pub(crate) fn track<F>(&self, tool: &str, arguments: &Value, action: F) -> Value
    where
        F: FnOnce() -> Value,
    {
        let Some(key) = request_key(tool, arguments) else {
            return action();
        };
        let now = SystemTime::now();
        let request_hash = hash_text(&key);
        if let Some(previous) = self.entries.lock().unwrap().get(&key).cloned() {
            if now
                .duration_since(previous.returned_at)
                .unwrap_or_default()
                <= TTL
            {
                return json!({
                    "status": "duplicate",
                    "duplicate": true,
                    "tool": tool,
                    "path": arguments["path"],
                    "requestHash": request_hash,
                    "previousReturnedAt": system_time_millis(previous.returned_at),
                    "omittedResponseChars": previous.response_chars,
                    "message": "This exact read was already returned from the same file version. The original response remains in context.",
                    "hint": "Change the requested range or arguments to read again. Any source-file change automatically invalidates this marker."
                });
            }
            self.entries.lock().unwrap().remove(&key);
        }

        let response = action();
        if response["status"] == "success" {
            let entry = Entry {
                returned_at: now,
                response_chars: response.to_string().chars().count(),
            };
            let mut entries = self.entries.lock().unwrap();
            entries.insert(key, entry);
            entries.retain(|_, entry| {
                now.duration_since(entry.returned_at).unwrap_or_default() <= TTL
            });
            if entries.len() > MAX_ENTRIES {
                entries.clear();
            }
        }
        response
    }
}

fn request_key(tool: &str, arguments: &Value) -> Option<String> {
    let path = arguments.get("path")?.as_str()?;
    let metadata = fs::metadata(path).ok()?;
    if !metadata.is_file() {
        return None;
    }
    let canonical = fs::canonicalize(path).ok()?;
    let modified = metadata
        .modified()
        .ok()
        .and_then(|time| time.duration_since(UNIX_EPOCH).ok())
        .map(|duration| format!("{}:{}", duration.as_secs(), duration.subsec_nanos()))
        .unwrap_or_else(|| "unknown".to_string());
    let request = serde_json::to_string(arguments).ok()?;
    Some(format!(
        "{tool}\0{}\0{}:{modified}\0{request}",
        canonical.to_string_lossy(),
        metadata.len()
    ))
}

fn hash_text(text: &str) -> String {
    let mut hasher = DefaultHasher::new();
    text.hash(&mut hasher);
    format!("{:016x}", hasher.finish())
}

fn system_time_millis(time: SystemTime) -> u128 {
    time.duration_since(UNIX_EPOCH)
        .map(|duration| duration.as_millis())
        .unwrap_or_default()
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn duplicate_reads_are_compact_and_file_changes_invalidate_them() {
        let root = std::env::temp_dir().join(format!(
            "zuma-read-guard-{}",
            SystemTime::now()
                .duration_since(UNIX_EPOCH)
                .unwrap()
                .as_nanos()
        ));
        fs::create_dir_all(&root).unwrap();
        let path = root.join("sample.txt");
        fs::write(&path, "alpha\nbeta\n").unwrap();
        let arguments = json!({ "path": path.to_string_lossy(), "startLine": 1, "endLine": 2 });
        let guard = RedundantReadGuard::new();

        let first = guard.track("read_section", &arguments, || json!({ "status": "success", "text": "alpha\nbeta" }));
        assert_eq!(first["status"], "success");
        let repeated = guard.track("read_section", &arguments, || panic!("duplicate read should not execute"));
        assert_eq!(repeated["status"], "duplicate");
        assert!(repeated["omittedResponseChars"].as_u64().unwrap() > 0);

        fs::write(&path, "alpha\nbeta\ngamma\n").unwrap();
        let refreshed = guard.track("read_section", &arguments, || json!({ "status": "success", "text": "alpha\nbeta\ngamma" }));
        assert_eq!(refreshed["status"], "success");
        fs::remove_dir_all(root).unwrap();
    }
}
