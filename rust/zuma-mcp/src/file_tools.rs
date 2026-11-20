use regex::{Regex, RegexBuilder};
use serde_json::{Map, Value, json};
use std::cmp;
use std::collections::{HashMap, HashSet};
use std::ffi::OsStr;
use std::fs::{self, File};
use std::io::{Read, Write};
use std::path::{Path, PathBuf};
use std::process::{Command, Stdio};
use std::sync::OnceLock;
use std::time::Instant;

use crate::OutlineEntry;

const DEFAULT_LOG_PATTERN: &str = "error|fail|exception|fatal|warn|エラー|警告|失敗|例外";
const SKIPPED_DIRECTORIES: [&str; 5] = [".git", "node_modules", ".venv", "bin", "obj"];

fn read_lines(path: &Path) -> Result<Vec<String>, String> {
    let bytes = fs::read(path).map_err(|error| error.to_string())?;
    let text = if bytes.starts_with(&[0xff, 0xfe]) {
        let words = bytes[2..]
            .as_chunks::<2>()
            .0
            .iter()
            .map(|pair| u16::from_le_bytes(*pair))
            .collect::<Vec<_>>();
        String::from_utf16(&words).map_err(|error| error.to_string())?
    } else if bytes.starts_with(&[0xfe, 0xff]) {
        let words = bytes[2..]
            .as_chunks::<2>()
            .0
            .iter()
            .map(|pair| u16::from_be_bytes(*pair))
            .collect::<Vec<_>>();
        String::from_utf16(&words).map_err(|error| error.to_string())?
    } else if let Ok(text) = std::str::from_utf8(&bytes) {
        text.strip_prefix('\u{feff}').unwrap_or(text).to_string()
    } else {
        encoding_rs::SHIFT_JIS.decode(&bytes).0.to_string()
    };
    Ok(text.lines().map(str::to_owned).collect())
}

fn validate_file(path: &Path) -> Result<(), String> {
    if !path.is_absolute() || !path.is_file() {
        return Err("Path must be an existing absolute file path.".to_string());
    }
    let bytes = fs::metadata(path).map_err(|error| error.to_string())?.len();
    if bytes > 50 * 1024 * 1024 {
        return Err("File exceeds 50 MB limit.".to_string());
    }
    Ok(())
}

fn indentation_width(line: &str) -> usize {
    line.chars()
        .take_while(|character| character.is_whitespace())
        .map(|character| if character == '\t' { 4 } else { 1 })
        .sum()
}

#[allow(clippy::type_complexity)]
fn code_regexes() -> &'static (
    Regex,
    Regex,
    Regex,
    Regex,
    Regex,
    Regex,
    Regex,
    Regex,
    Regex,
    Regex,
    Regex,
) {
    static REGEXES: OnceLock<(
        Regex,
        Regex,
        Regex,
        Regex,
        Regex,
        Regex,
        Regex,
        Regex,
        Regex,
        Regex,
        Regex,
    )> = OnceLock::new();
    REGEXES.get_or_init(|| (
        Regex::new(r"^(?i)(?:(?:public|private|protected|internal|export|abstract|sealed|static|partial|default|async)\s+|\s)*(?:class|interface|struct|record|enum|namespace|module|type)\s+\w+").unwrap(),
        Regex::new(r"^(?i)(?:export\s+)?(?:async\s+)?function\s*\*?\s*\w+").unwrap(),
        Regex::new(r"^(?i)(?:(?:public|private|protected|internal|static|async|override|virtual|sealed|abstract)\s+|\s)+[\w<>\[\],\s?]+\s+\w+\s*\(").unwrap(),
        Regex::new(r"^(?i)(?:(?:public|private|protected|internal|static|override|virtual|sealed|abstract)\s+|\s)+[\w<>\[\],\s?]+\s+\w+\s*=>").unwrap(),
        Regex::new(r"^(?:async\s+)?def\s+\w+").unwrap(),
        Regex::new(r"^class\s+\w+").unwrap(),
        Regex::new(r"^func\s+").unwrap(),
        Regex::new(r"^(?:pub\s+)?fn\s+\w+").unwrap(),
        Regex::new(r"^function\s+[\w-]+").unwrap(),
        Regex::new(r"^(?i)(?:(?:Public|Private|Protected|Friend|Shared)\s+|\s)*(?:Sub|Function|Class|Module|Property)\s+\w+").unwrap(),
        Regex::new(r"^(?:if|for|while|switch|catch|return|new|else)\b").unwrap(),
    ))
}

fn compute_hierarchical_ends(entries: &mut [OutlineEntry], total_lines: usize) {
    for index in 0..entries.len() {
        let mut end = total_lines;
        if let Some(next) = entries[index + 1..]
            .iter()
            .find(|entry| entry.level <= entries[index].level)
        {
            end = next.start.saturating_sub(1);
        }
        entries[index].end = end.max(entries[index].start);
    }
}

pub fn code_outline(lines: &[String], python: bool) -> Vec<OutlineEntry> {
    let (
        type_re,
        function_re,
        method_re,
        expression_re,
        python_def_re,
        class_re,
        go_re,
        rust_re,
        pwsh_re,
        vb_re,
        control_re,
    ) = code_regexes();
    let mut entries = Vec::new();
    let mut decorator_start: Option<usize> = None;
    let mut decorator_indent = 0;
    let mut decorator_depth: i32 = 0;

    for (index, line) in lines.iter().enumerate() {
        let line_number = index + 1;
        let trimmed = line.trim();
        let width = indentation_width(line);

        if python && (decorator_depth > 0 || trimmed.starts_with('@')) {
            if decorator_depth == 0 {
                if decorator_start.is_none() || width != decorator_indent {
                    decorator_start = Some(line_number);
                }
                decorator_indent = width;
            }
            let mut quote = '\0';
            let mut escaped = false;
            for character in trimmed.chars() {
                if quote != '\0' {
                    if escaped {
                        escaped = false;
                    } else if character == '\\' {
                        escaped = true;
                    } else if character == quote {
                        quote = '\0';
                    }
                    continue;
                }
                if character == '#' {
                    break;
                }
                if character == '\'' || character == '"' {
                    quote = character;
                } else if character == '(' || character == '[' || character == '{' {
                    decorator_depth += 1;
                } else if character == ')' || character == ']' || character == '}' {
                    decorator_depth = (decorator_depth - 1).max(0);
                }
            }
            continue;
        }

        if trimmed.is_empty()
            || trimmed.starts_with("//")
            || trimmed.starts_with('\'')
            || control_re.is_match(trimmed)
        {
            continue;
        }

        let matched = type_re.is_match(trimmed)
            || function_re.is_match(trimmed)
            || (method_re.is_match(trimmed) && (!trimmed.ends_with(';') || trimmed.contains("=>")))
            || expression_re.is_match(trimmed)
            || python_def_re.is_match(trimmed)
            || class_re.is_match(trimmed)
            || go_re.is_match(trimmed)
            || rust_re.is_match(trimmed)
            || pwsh_re.is_match(trimmed)
            || vb_re.is_match(trimmed);
        if matched {
            let title: String = trimmed.chars().take(120).collect();
            let start = if python
                && width == decorator_indent
                && (python_def_re.is_match(trimmed) || class_re.is_match(trimmed))
            {
                decorator_start.unwrap_or(line_number)
            } else {
                line_number
            };
            entries.push(OutlineEntry {
                start,
                end: line_number,
                level: (width / 4) + 1,
                title,
            });
        }
        if python {
            decorator_start = None;
            decorator_indent = 0;
        }
    }

    compute_hierarchical_ends(&mut entries, lines.len());
    entries
}

fn integer_argument(arguments: &Value, name: &str, default: usize) -> usize {
    arguments
        .get(name)
        .and_then(Value::as_u64)
        .map(|value| value as usize)
        .unwrap_or(default)
}

fn boolean_argument(arguments: &Value, name: &str, default: bool) -> bool {
    arguments
        .get(name)
        .and_then(Value::as_bool)
        .unwrap_or(default)
}

pub fn summarize_log(arguments: &Value) -> Value {
    let path_text = match arguments.get("path").and_then(Value::as_str) {
        Some(path) => path,
        None => return json!({ "status": "error", "message": "path must be a string" }),
    };
    let path = Path::new(path_text);
    if let Err(message) = validate_file(path) {
        return json!({ "status": "error", "message": message });
    }
    let lines = match read_lines(path) {
        Ok(lines) => lines,
        Err(message) => return json!({ "status": "error", "message": message }),
    };
    let pattern = arguments
        .get("pattern")
        .and_then(Value::as_str)
        .unwrap_or(DEFAULT_LOG_PATTERN);
    let regex = match RegexBuilder::new(pattern)
        .case_insensitive(true)
        .unicode(true)
        .build()
    {
        Ok(regex) => regex,
        Err(error) => {
            return json!({ "status": "error", "message": format!("Invalid regex pattern: {error}") });
        }
    };
    let zero_regex = Regex::new(r"(?i)\b0\s*(?:(?:件|個)\s*)?(?:の\s*)?(?:errors?(?:\(s\))?|warnings?(?:\(s\))?|エラー|警告|失敗|例外)").unwrap();
    let digit_regex = Regex::new(r"\d+").unwrap();
    let ansi_regex = ansi_regex();
    let context_lines = integer_argument(arguments, "contextLines", 1).clamp(0, 5);
    let tail_lines = integer_argument(arguments, "tailLines", 20).clamp(0, 200);
    let max_groups = integer_argument(arguments, "maxGroups", 30).clamp(1, 200);
    let max_chars = integer_argument(arguments, "maxChars", 8000).clamp(2000, 50000);
    let mut groups: Vec<LogGroup> = Vec::new();
    let mut group_indexes: HashMap<String, usize> = HashMap::new();
    let mut matched_lines = 0;

    for (index, line) in lines.iter().enumerate() {
        let clean = ansi_regex.replace_all(line, "").trim().to_string();
        let searchable = if pattern == DEFAULT_LOG_PATTERN {
            zero_regex.replace_all(&clean, "").to_string()
        } else {
            clean.clone()
        };
        if !regex.is_match(&searchable) {
            continue;
        }
        matched_lines += 1;
        let key = digit_regex.replace_all(&clean, "#").to_string();
        if let Some(group_index) = group_indexes.get(&key).copied() {
            let group = &mut groups[group_index];
            group.count += 1;
            group.last_line = index + 1;
            continue;
        }

        let mut context = Vec::new();
        if context_lines > 0 {
            let start = index.saturating_sub(context_lines);
            let end = cmp::min(lines.len() - 1, index + context_lines);
            for (context_index, context_line) in lines.iter().enumerate().take(end + 1).skip(start)
            {
                if context_index == index {
                    continue;
                }
                let text = ansi_regex
                    .replace_all(context_line, "")
                    .trim()
                    .chars()
                    .take(300)
                    .collect::<String>();
                context.push(format!("{}: {text}", context_index + 1));
            }
        }
        group_indexes.insert(key, groups.len());
        groups.push(LogGroup {
            count: 1,
            first_line: index + 1,
            last_line: index + 1,
            text: clean.chars().take(300).collect(),
            context,
        });
    }

    let group_count = groups.len();
    let groups_truncated = groups.len() > max_groups;
    let mut selected_groups = groups.into_iter().take(max_groups).collect::<Vec<_>>();
    let mut selected_groups_truncated = groups_truncated;
    let mut tail = Vec::new();
    if tail_lines > 0 && !lines.is_empty() {
        let start = lines.len().saturating_sub(tail_lines);
        for (index, line) in lines.iter().enumerate().skip(start) {
            let text = ansi_regex
                .replace_all(line, "")
                .trim()
                .chars()
                .take(300)
                .collect::<String>();
            tail.push(format!("{}: {text}", index + 1));
        }
    }

    let mut truncated = false;
    loop {
        let response = log_payload(
            path_text,
            lines.len(),
            matched_lines,
            group_count,
            &selected_groups,
            selected_groups_truncated,
            &tail,
            truncated,
        );
        if serde_json::to_string(&response).unwrap().len() <= max_chars {
            return response;
        }
        truncated = true;
        if let Some(group) = selected_groups
            .iter_mut()
            .rev()
            .find(|group| !group.context.is_empty())
        {
            group.context.clear();
            continue;
        }
        if !selected_groups.is_empty() {
            selected_groups.pop();
            selected_groups_truncated = true;
            continue;
        }
        if !tail.is_empty() {
            tail.remove(0);
            continue;
        }
        return log_payload(
            path_text,
            lines.len(),
            matched_lines,
            group_count,
            &[],
            true,
            &[],
            truncated,
        );
    }
}

#[derive(Clone)]
struct LogGroup {
    count: usize,
    first_line: usize,
    last_line: usize,
    text: String,
    context: Vec<String>,
}

fn ansi_regex() -> &'static Regex {
    static REGEX: OnceLock<Regex> = OnceLock::new();
    REGEX.get_or_init(|| {
        Regex::new(r"\x1B(?:\[[0-?]*[ -/]*[@-~]|\][^\x07\x1B]*(?:\x07|\x1B\\))").unwrap()
    })
}

#[allow(clippy::too_many_arguments)]
fn log_payload(
    path: &str,
    total_lines: usize,
    matched_lines: usize,
    group_count: usize,
    groups: &[LogGroup],
    groups_truncated: bool,
    tail: &[String],
    truncated: bool,
) -> Value {
    json!({
        "status": "success",
        "path": path,
        "encoding": "utf-8",
        "totalLines": total_lines,
        "matchedLines": matched_lines,
        "groupCount": group_count,
        "groups": groups.iter().map(|group| json!({
            "count": group.count,
            "firstLine": group.first_line,
            "lastLine": group.last_line,
            "text": group.text,
            "context": group.context
        })).collect::<Vec<_>>(),
        "groupsTruncated": groups_truncated,
        "tail": tail,
        "truncated": truncated
    })
}

pub fn search_files(arguments: &Value) -> Value {
    let path_text = match arguments.get("path").and_then(Value::as_str) {
        Some(path) => path,
        None => {
            return json!({ "status": "error", "message": "Path must be an existing absolute directory or file path." });
        }
    };
    let root = Path::new(path_text);
    if !root.is_absolute() || (!root.is_dir() && !root.is_file()) {
        return json!({ "status": "error", "message": "Path must be an existing absolute directory or file path." });
    }
    let pattern = match arguments.get("pattern").and_then(Value::as_str) {
        Some(pattern) => pattern,
        None => return json!({ "status": "error", "message": "pattern must be a string" }),
    };
    let literal = boolean_argument(arguments, "literal", false);
    let expression = if literal {
        regex::escape(pattern)
    } else {
        pattern.to_string()
    };
    let regex = match RegexBuilder::new(&expression)
        .case_insensitive(!boolean_argument(arguments, "caseSensitive", false))
        .unicode(true)
        .build()
    {
        Ok(regex) => regex,
        Err(error) => {
            return json!({ "status": "error", "message": format!("Invalid regex pattern: {error}") });
        }
    };
    let file_name = arguments.get("fileName").and_then(Value::as_str);
    let max_results = integer_argument(arguments, "maxResults", 30).clamp(1, 100);
    let max_per_file = integer_argument(arguments, "maxPerFile", 20).clamp(1, 100);
    let context_lines = integer_argument(arguments, "contextLines", 0).clamp(0, 5);
    let excludes = arguments
        .get("exclude")
        .and_then(Value::as_array)
        .map(|values| {
            values
                .iter()
                .filter_map(Value::as_str)
                .map(str::to_owned)
                .collect::<Vec<_>>()
        })
        .unwrap_or_default();
    let base = if root.is_dir() {
        root.to_path_buf()
    } else {
        root.parent().unwrap().to_path_buf()
    };
    let started = Instant::now();
    let mut files = enumerate_files(root, file_name);
    let mut stopped_early = false;
    if files.len() > 50_000 {
        files.truncate(50_000);
        stopped_early = true;
    }
    let ignored = match git_ignored_files(&base, &files) {
        Ok(ignored) => ignored,
        Err(message) => return json!({ "status": "error", "message": message }),
    };
    let mut found = Vec::<FileMatches>::new();
    let mut files_scanned = 0;
    let mut files_matched = 0;
    let mut total_matches = 0;

    for file in files {
        if files_scanned >= 50_000 || started.elapsed().as_secs_f64() > 20.0 {
            stopped_early = true;
            break;
        }
        let relative = file
            .strip_prefix(&base)
            .unwrap_or(&file)
            .to_string_lossy()
            .replace('\\', "/");
        if ignored.contains(&file)
            || excludes.iter().any(|glob| {
                if glob.contains('/') || glob.contains('\\') {
                    glob_matches(glob, &relative)
                } else {
                    let base_name = file.file_name().unwrap_or_default().to_string_lossy();
                    glob_matches(glob, base_name.as_ref())
                }
            })
        {
            continue;
        }
        if fs::metadata(&file)
            .map(|metadata| metadata.len())
            .unwrap_or(u64::MAX)
            > 5 * 1024 * 1024
            || is_binary(&file).unwrap_or(true)
        {
            continue;
        }
        let lines = match read_lines(&file) {
            Ok(lines) => lines,
            Err(_) => continue,
        };
        files_scanned += 1;
        let mut current: Option<usize> = None;
        for (index, line) in lines.iter().enumerate() {
            if !regex.is_match(line) {
                continue;
            }
            total_matches += 1;
            if current.is_none() {
                files_matched += 1;
                if found.len() < 40 {
                    found.push(FileMatches {
                        file: relative.clone(),
                        count: 0,
                        hits: Vec::new(),
                        shown: 0,
                    });
                    current = Some(found.len() - 1);
                } else {
                    current = Some(usize::MAX);
                }
            }
            let file_index = current.unwrap();
            if file_index == usize::MAX {
                continue;
            }
            let item = &mut found[file_index];
            item.count += 1;
            if item.hits.len() >= max_per_file {
                continue;
            }
            let before = (index.saturating_sub(context_lines)..index)
                .map(|line_index| format_line(&lines, line_index))
                .collect();
            let after_start = index + 1;
            let after_end = cmp::min(lines.len(), after_start + context_lines);
            let after = (after_start..after_end)
                .map(|line_index| format_line(&lines, line_index))
                .collect();
            item.hits.push(SearchHit {
                text: format_line(&lines, index),
                line: index + 1,
                before,
                after,
            });
        }
        // Files after the listing cap still count as matches, while matching files retained here keep the full count.
    }

    let mut shown = 0;
    while shown < max_results {
        let mut added = false;
        for item in &mut found {
            if item.shown >= item.hits.len() || shown >= max_results {
                continue;
            }
            item.shown += 1;
            shown += 1;
            added = true;
        }
        if !added {
            break;
        }
    }

    let mut response = search_payload(
        &found,
        files_scanned,
        files_matched,
        total_matches,
        shown,
        stopped_early,
        context_lines > 0,
    );
    while serde_json::to_string(&response).unwrap().len() > 8_000 {
        let Some(index) = (0..found.len()).rev().find(|index| found[*index].shown > 0) else {
            break;
        };
        shown = shown.saturating_sub(found[index].shown);
        found[index].shown = 0;
        response = search_payload(
            &found,
            files_scanned,
            files_matched,
            total_matches,
            shown,
            stopped_early,
            context_lines > 0,
        );
    }
    response
}

#[derive(Clone)]
struct SearchHit {
    text: String,
    line: usize,
    before: Vec<String>,
    after: Vec<String>,
}

#[derive(Clone)]
struct FileMatches {
    file: String,
    count: usize,
    hits: Vec<SearchHit>,
    shown: usize,
}

fn format_line(lines: &[String], index: usize) -> String {
    let text = lines[index].trim_end();
    let rendered = if text.chars().count() > 200 {
        format!("{}...", text.chars().take(200).collect::<String>())
    } else {
        text.to_string()
    };
    format!("{}| {rendered}", index + 1)
}

fn search_payload(
    found: &[FileMatches],
    files_scanned: usize,
    files_matched: usize,
    total_matches: usize,
    shown: usize,
    stopped_early: bool,
    with_context: bool,
) -> Value {
    let files = found
        .iter()
        .map(|item| {
            let mut value = Map::new();
            value.insert("file".to_string(), Value::String(item.file.clone()));
            value.insert("count".to_string(), json!(item.count));
            if item.shown > 0 {
                value.insert(
                    "matches".to_string(),
                    json!(
                        item.hits
                            .iter()
                            .take(item.shown)
                            .map(|hit| hit.text.clone())
                            .collect::<Vec<_>>()
                    ),
                );
            }
            if item.count > item.shown {
                value.insert("omitted".to_string(), json!(item.count - item.shown));
            }
            if with_context && item.shown > 0 {
                value.insert(
                    "context".to_string(),
                    json!(
                        item.hits
                            .iter()
                            .take(item.shown)
                            .map(|hit| json!({
                                "line": hit.line,
                                "before": hit.before,
                                "after": hit.after
                            }))
                            .collect::<Vec<_>>()
                    ),
                );
            }
            Value::Object(value)
        })
        .collect::<Vec<_>>();
    let mut value = json!({
        "status": "success",
        "filesScanned": files_scanned,
        "filesMatched": files_matched,
        "totalMatches": total_matches,
        "files": files
    });
    if shown < total_matches || files_matched > found.len() {
        value["truncated"] = json!(true);
    }
    if stopped_early {
        value["stoppedEarly"] = json!(true);
    }
    value
}

fn is_binary(path: &Path) -> Result<bool, String> {
    let mut file = File::open(path).map_err(|error| error.to_string())?;
    let mut buffer = [0_u8; 4096];
    let count = file.read(&mut buffer).map_err(|error| error.to_string())?;
    let bytes = &buffer[..count];
    let utf16 = count >= 2
        && ((bytes[0] == 0xff && bytes[1] == 0xfe) || (bytes[0] == 0xfe && bytes[1] == 0xff));
    Ok(!utf16 && bytes.contains(&0))
}

fn enumerate_files(root: &Path, file_name: Option<&str>) -> Vec<PathBuf> {
    if root.is_file() {
        return if file_name.is_none_or(|pattern| {
            glob_matches(
                pattern,
                root.file_name()
                    .unwrap_or_default()
                    .to_string_lossy()
                    .as_ref(),
            )
        }) {
            vec![root.to_path_buf()]
        } else {
            Vec::new()
        };
    }
    let mut files = Vec::new();
    let mut pending = vec![root.to_path_buf()];
    while let Some(directory) = pending.pop() {
        let Ok(entries) = fs::read_dir(directory) else {
            continue;
        };
        let mut children = entries.filter_map(Result::ok).collect::<Vec<_>>();
        children.sort_by_key(|entry| entry.file_name());
        let mut directories = Vec::new();
        for entry in &children {
            let path = entry.path();
            let Ok(file_type) = entry.file_type() else {
                continue;
            };
            if file_type.is_symlink() {
                continue;
            }
            if file_type.is_dir() {
                if !SKIPPED_DIRECTORIES
                    .iter()
                    .any(|name| entry.file_name() == OsStr::new(name))
                {
                    directories.push(path);
                }
            } else if file_name.is_none_or(|pattern| {
                glob_matches(pattern, entry.file_name().to_string_lossy().as_ref())
            }) {
                files.push(path);
            }
        }
        for directory in directories.into_iter().rev() {
            pending.push(directory);
        }
    }
    files
}

fn glob_matches(pattern: &str, value: &str) -> bool {
    let pattern = pattern.replace('\\', "/");
    let mut regex = String::from("^");
    let mut characters = pattern.chars().peekable();
    while let Some(character) = characters.next() {
        match character {
            '*' if characters.peek() == Some(&'*') => {
                characters.next();
                if characters.peek() == Some(&'/') {
                    characters.next();
                    regex.push_str("(?:.*/)?");
                } else {
                    regex.push_str(".*");
                }
            }
            '*' => regex.push_str("[^/]*"),
            '?' => regex.push_str("[^/]"),
            _ => regex.push_str(&regex::escape(&character.to_string())),
        }
    }
    regex.push('$');
    RegexBuilder::new(&regex)
        .case_insensitive(true)
        .build()
        .map(|regex| regex.is_match(value))
        .unwrap_or(false)
}

fn git_ignored_files(base: &Path, files: &[PathBuf]) -> Result<HashSet<PathBuf>, String> {
    let mut repository = Some(base.to_path_buf());
    while let Some(directory) = repository {
        if directory.join(".git").exists() {
            repository = Some(directory);
            break;
        }
        repository = directory.parent().map(Path::to_path_buf);
    }
    let Some(repository) = repository else {
        return Ok(HashSet::new());
    };
    if files.is_empty() {
        return Ok(HashSet::new());
    }
    let mut child = Command::new("git")
        .current_dir(&repository)
        .args(["check-ignore", "-z", "--stdin"])
        .stdin(Stdio::piped())
        .stdout(Stdio::piped())
        .stderr(Stdio::piped())
        .spawn()
        .map_err(|error| error.to_string())?;
    {
        let stdin = child
            .stdin
            .as_mut()
            .ok_or("git check-ignore stdin is unavailable")?;
        for file in files {
            let relative = file.strip_prefix(&repository).unwrap_or(file);
            stdin
                .write_all(relative.to_string_lossy().replace('\\', "/").as_bytes())
                .map_err(|error| error.to_string())?;
            stdin.write_all(&[0]).map_err(|error| error.to_string())?;
        }
    }
    let output = child
        .wait_with_output()
        .map_err(|error| error.to_string())?;
    if !matches!(output.status.code(), Some(0 | 1)) {
        return Err(format!(
            "git check-ignore failed: {}",
            String::from_utf8_lossy(&output.stderr)
        ));
    }
    Ok(output
        .stdout
        .split(|byte| *byte == 0)
        .filter(|entry| !entry.is_empty())
        .map(|entry| {
            repository
                .join(String::from_utf8_lossy(entry).replace('/', std::path::MAIN_SEPARATOR_STR))
        })
        .collect())
}
