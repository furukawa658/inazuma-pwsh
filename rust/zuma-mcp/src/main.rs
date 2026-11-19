use serde::Deserialize;
use serde_json::{Value, json};
use std::fs;
use std::io::{self, BufRead, Write};
use std::path::Path;

const VERSION: &str = env!("CARGO_PKG_VERSION");

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
        return json!({ "status": "error", "message": "query is not implemented in this Rust prototype; specify startLine." });
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
        { "name": "peek_file", "description": "Return a compact outline of a text file.", "inputSchema": { "type": "object", "properties": { "path": { "type": "string" }, "fullThreshold": { "type": "integer", "default": 200 }, "maxEntries": { "type": "integer", "default": 200 }, "maxLevel": { "type": "integer" } }, "required": ["path"] } },
        { "name": "read_section", "description": "Read only a line range from a text file.", "inputSchema": { "type": "object", "properties": { "path": { "type": "string" }, "startLine": { "type": "integer" }, "endLine": { "type": "integer" }, "maxChars": { "type": "integer", "default": 12000 } }, "required": ["path", "startLine"] } }
    ] })
}

fn call_tool(params: &Value) -> Value {
    let arguments = params.get("arguments").unwrap_or(&Value::Null);
    let payload = match params.get("name").and_then(Value::as_str) {
        Some("peek_file") => peek_file(arguments),
        Some("read_section") => read_section(arguments),
        _ => {
            return json!({ "content": [{ "type": "text", "text": "{\"status\":\"error\",\"message\":\"Tool not found.\"}" }], "isError": true });
        }
    };
    json!({ "content": [{ "type": "text", "text": payload.to_string() }] })
}

fn handle_request(request: Request) -> Option<Value> {
    let Some(id) = request.id else {
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
        "tools/call" => call_tool(&request.params),
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

fn main() -> io::Result<()> {
    let stdin = io::stdin();
    let stdout = io::stdout();
    let mut output = stdout.lock();

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
                writeln!(output, "{}", serde_json::to_string(&response)?)?;
                output.flush()?;
                continue;
            }
        };

        if let Some(response) = handle_request(request) {
            writeln!(output, "{}", serde_json::to_string(&response)?)?;
            output.flush()?;
        }
    }

    Ok(())
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
