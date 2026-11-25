use serde_json::Value;
use std::io::Write;
use std::process::{Command, Stdio};
use std::time::{SystemTime, UNIX_EPOCH};

fn call(requests: &[Value]) -> Vec<Value> {
    call_with_environment(requests, &[])
}

fn call_with_environment(requests: &[Value], environment: &[(&str, &str)]) -> Vec<Value> {
    let mut command = Command::new(env!("CARGO_BIN_EXE_zuma-mcp"));
    command
        .env("ZUMA_HANDOFF_ENABLED", "true")
        .stdin(Stdio::piped())
        .stdout(Stdio::piped());
    for (name, value) in environment {
        command.env(name, value);
    }
    let mut child = command.spawn().expect("Rust MCP server should start");

    {
        let stdin = child.stdin.as_mut().expect("stdin should be available");
        for request in requests {
            writeln!(stdin, "{request}").expect("request should be written");
        }
    }

    let output = child
        .wait_with_output()
        .expect("server should exit cleanly");
    assert!(
        output.status.success(),
        "{}",
        String::from_utf8_lossy(&output.stderr)
    );
    String::from_utf8(output.stdout)
        .expect("stdout should be UTF-8")
        .lines()
        .map(|line| serde_json::from_str(line).expect("response should be JSON"))
        .collect()
}

#[test]
fn array_tool_parameters_define_items() {
    let responses = call(&[
        serde_json::json!({
            "jsonrpc": "2.0",
            "id": 1,
            "method": "initialize",
            "params": { "protocolVersion": "2024-11-05", "capabilities": {}, "clientInfo": { "name": "test", "version": "1" } }
        }),
        serde_json::json!({
            "jsonrpc": "2.0",
            "method": "notifications/initialized",
            "params": {}
        }),
        serde_json::json!({
            "jsonrpc": "2.0",
            "id": 2,
            "method": "tools/list",
            "params": {}
        }),
    ]);

    let initialize_response = responses
        .iter()
        .find(|response| response["id"] == 1)
        .expect("initialize response should be returned");
    assert_eq!(
        initialize_response["result"]["capabilities"]["extensions"]["io.modelcontextprotocol/tasks"],
        serde_json::json!({})
    );
    assert!(
        initialize_response["result"]["instructions"]
            .as_str()
            .unwrap()
            .contains("set_task_anchor")
    );
    let list_response = responses
        .iter()
        .find(|response| response["id"] == 2)
        .expect("tools/list response should be returned");
    let tools = list_response["result"]["tools"].as_array().unwrap();
    assert_eq!(tools.len(), 7);
    for name in [
        "zuma_task",
        "zuma_shell",
        "zuma_execute",
        "zuma_read",
        "zuma_context",
        "zuma_gate",
        "zuma_handoff",
    ] {
        assert!(tools.iter().any(|tool| tool["name"] == name), "missing router {name}");
    }
    for name in [
        "zuma_task",
        "zuma_shell",
        "zuma_execute",
        "zuma_read",
        "zuma_context",
        "zuma_gate",
        "zuma_handoff",
    ] {
        let tool = tools.iter().find(|tool| tool["name"] == name).unwrap();
        assert_eq!(tool["inputSchema"]["properties"]["operation"]["type"], "string", "{name} must route by operation");
    }
    assert!(
        tools.iter().all(|tool| !tool["name"].as_str().unwrap().starts_with("run_")),
        "legacy run_* tools must be hidden behind zuma_execute"
    );
    let execute = tools
        .iter()
        .find(|tool| tool["name"] == "zuma_execute")
        .unwrap();
    assert_eq!(execute["inputSchema"]["properties"]["operation"]["type"], "string");
    assert_eq!(execute["inputSchema"]["properties"]["regressionsOnly"]["type"], "boolean");
    for tool in tools {
        for (name, schema) in tool["inputSchema"]["properties"].as_object().unwrap() {
            if schema["type"] == "array" {
                assert!(
                    schema.get("items").is_some(),
                    "{}.{} is an array without items",
                    tool["name"],
                    name
                );
            }
        }
    }
}

#[test]
fn initializes_and_calls_file_tools() {
    let unique = SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .unwrap()
        .as_nanos();
    let path = std::env::temp_dir().join(format!("zuma-rust-{unique}.md"));
    std::fs::write(&path, "# First\nbody\n## Second\nlast\n").unwrap();

    let responses = call(&[
        serde_json::json!({
            "jsonrpc": "2.0",
            "id": 1,
            "method": "initialize",
            "params": { "protocolVersion": "2024-11-05", "capabilities": {}, "clientInfo": { "name": "test", "version": "1" } }
        }),
        serde_json::json!({
            "jsonrpc": "2.0",
            "method": "notifications/initialized",
            "params": {}
        }),
        serde_json::json!({
            "jsonrpc": "2.0",
            "id": 2,
            "method": "tools/call",
            "params": { "name": "peek_file", "arguments": { "path": path.to_string_lossy() } }
        }),
    ]);

    std::fs::remove_file(path).unwrap();
    assert_eq!(responses.len(), 2);
    let initialize = responses
        .iter()
        .find(|response| response["id"] == 1)
        .expect("initialize response should be returned");
    assert_eq!(initialize["result"]["serverInfo"]["name"], "Zuma");
    let tool_response = responses
        .iter()
        .find(|response| response["id"] == 2)
        .expect("peek_file response should be returned");
    let payload: Value = serde_json::from_str(
        tool_response["result"]["content"][0]["text"]
            .as_str()
            .unwrap(),
    )
    .unwrap();
    assert_eq!(
        payload["entries"],
        serde_json::json!(["1-4 1 First", "3-4 2 Second"])
    );
}

fn tool_payload(responses: &[Value], id: u64) -> Value {
    let response = responses
        .iter()
        .find(|response| response["id"] == id)
        .expect("tool response should be returned");
    assert_eq!(response["error"], Value::Null, "{response}");
    assert_ne!(response["result"]["isError"], true, "{response}");
    serde_json::from_str(response["result"]["content"][0]["text"].as_str().unwrap()).unwrap()
}

#[test]
fn reminds_each_tool_about_the_active_task_anchor() {
    let unique = SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .unwrap()
        .as_nanos();
    let root = std::env::temp_dir().join(format!("zuma-anchor-{unique}"));
    std::fs::create_dir_all(&root).unwrap();
    let source = root.join("sample.md");
    let outside = root.join("../outside.md");
    std::fs::write(&source, "# Target\n").unwrap();
    std::fs::write(&outside, "# Outside\n").unwrap();

    let responses = call(&[
        serde_json::json!({ "jsonrpc": "2.0", "id": 1, "method": "initialize", "params": { "protocolVersion": "2024-11-05", "capabilities": {}, "clientInfo": { "name": "test", "version": "1" } } }),
        serde_json::json!({ "jsonrpc": "2.0", "id": 2, "method": "tools/call", "params": { "name": "set_task_anchor", "arguments": { "goal": "Finish the anchor feature", "target": "Zuma task reminders", "acceptanceCriteria": ["Responses contain the target"], "outOfScope": ["UI redesign"], "allowedPaths": [root.to_string_lossy()], "sessionId": "anchor-test" } } }),
        serde_json::json!({ "jsonrpc": "2.0", "id": 3, "method": "tools/call", "params": { "name": "peek_file", "arguments": { "path": outside.to_string_lossy() } } }),
        serde_json::json!({ "jsonrpc": "2.0", "id": 4, "method": "tools/call", "params": { "name": "get_task_anchor", "arguments": {} } }),
        serde_json::json!({ "jsonrpc": "2.0", "id": 5, "method": "tools/call", "params": { "name": "clear_task_anchor", "arguments": {} } }),
        serde_json::json!({ "jsonrpc": "2.0", "id": 6, "method": "tools/call", "params": { "name": "peek_file", "arguments": { "path": source.to_string_lossy() } } }),
    ]);

    let set = tool_payload(&responses, 2);
    assert_eq!(set["taskAnchor"]["target"], "Zuma task reminders");
    let reminder = tool_payload(&responses, 3);
    assert_eq!(reminder["taskAnchor"]["goal"], "Finish the anchor feature");
    assert!(reminder["taskAnchor"].get("scopeWarnings").is_none());
    assert_eq!(tool_payload(&responses, 4)["status"], "active");
    assert_eq!(tool_payload(&responses, 5)["status"], "cleared");
    assert!(tool_payload(&responses, 6).get("taskAnchor").is_none());

    std::fs::remove_file(outside).unwrap();
    std::fs::remove_dir_all(root).unwrap();
}

#[test]
fn handles_code_search_and_log_tools_without_sidecar() {
    let unique = SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .unwrap()
        .as_nanos();
    let root = std::env::temp_dir().join(format!("zuma-rust-tools-{unique}"));
    std::fs::create_dir_all(root.join("node_modules")).unwrap();
    assert!(
        Command::new("git")
            .args(["init", "-q"])
            .current_dir(&root)
            .status()
            .unwrap()
            .success()
    );
    let source = root.join("sample.cs");
    let log = root.join("build.log");
    std::fs::write(&source, "public class Sample\n{\n    public int Add(int a, int b) => a + b;\n    public int X => 1;\n}\n").unwrap();
    std::fs::write(root.join(".gitignore"), "ignored.txt\n").unwrap();
    std::fs::write(root.join("needle.txt"), "before\nneedle\nneedle\n").unwrap();
    std::fs::write(root.join("ignored.txt"), "needle\n").unwrap();
    std::fs::write(root.join("node_modules/skip.txt"), "needle\n").unwrap();
    std::fs::write(
        &log,
        [
            "0 エラー",
            "0 個の警告",
            "0 Error(s)",
            "エラー 発生",
            "警告 発生",
            "処理失敗",
            "例外 発生",
            "0 エラー 1 警告",
        ]
        .join("\n"),
    )
    .unwrap();

    let responses = call(&[
        serde_json::json!({ "jsonrpc": "2.0", "id": 1, "method": "initialize", "params": { "protocolVersion": "2024-11-05", "capabilities": {}, "clientInfo": { "name": "test", "version": "1" } } }),
        serde_json::json!({ "jsonrpc": "2.0", "method": "notifications/initialized", "params": {} }),
        serde_json::json!({ "jsonrpc": "2.0", "id": 2, "method": "tools/call", "params": { "name": "peek_file", "arguments": { "path": source.to_string_lossy() } } }),
        serde_json::json!({ "jsonrpc": "2.0", "id": 3, "method": "tools/call", "params": { "name": "search_files", "arguments": { "path": root.to_string_lossy(), "pattern": "needle", "literal": true, "maxPerFile": 1, "maxResults": 1 } } }),
        serde_json::json!({ "jsonrpc": "2.0", "id": 4, "method": "tools/call", "params": { "name": "summarize_log", "arguments": { "path": log.to_string_lossy(), "contextLines": 0, "tailLines": 2 } } }),
    ]);

    let outline = tool_payload(&responses, 2);
    assert_eq!(
        outline["entries"],
        serde_json::json!([
            "1-5 1 public class Sample",
            "3-3 2 public int Add(int a, int b) => a + b;",
            "4-5 2 public int X => 1;"
        ])
    );
    let search = tool_payload(&responses, 3);
    assert_eq!(search["totalMatches"], 2);
    assert_eq!(search["files"][0]["file"], "needle.txt");
    assert_eq!(search["files"][0]["matches"].as_array().unwrap().len(), 1);
    assert_eq!(search["files"][0]["omitted"], 1);
    let summary = tool_payload(&responses, 4);
    assert_eq!(summary["matchedLines"], 5);
    assert_eq!(summary["groups"].as_array().unwrap().len(), 5);
    assert_eq!(
        summary["tail"],
        serde_json::json!(["7: 例外 発生", "8: 0 エラー 1 警告"])
    );

    std::fs::remove_dir_all(root).unwrap();
}

#[test]
fn prepares_a_bounded_handoff_from_a_synthetic_session() {
    let unique = SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .unwrap()
        .as_nanos();
    let root = std::env::temp_dir().join(format!("zuma-handoff-{unique}"));
    std::fs::create_dir_all(&root).unwrap();
    let session = root.join("session.jsonl");
    let seed = serde_json::json!({
        "kind": 0,
        "v": {
            "version": 3,
            "customTitle": "Synthetic handoff",
            "requests": [{
                "message": { "text": "Implement safe handoff generation" },
                "response": [],
                "promptTokens": 1
            }],
            "inputState": { "inputText": "Continue with the tests" }
        }
    });
    let long_next_action = format!(
        "Next: update the Marketplace display name {}",
        "x".repeat(1800)
    );
    let replacement = serde_json::json!({
        "kind": 1,
        "k": ["inputState", "inputText"],
        "v": long_next_action
    });
    let appended_request = serde_json::json!({
        "kind": 2,
        "k": ["requests"],
        "v": [{
            "message": { "text": "Keep the token secret=" },
            "response": []
        }]
    });
    std::fs::write(
        &session,
        [seed, replacement, appended_request]
            .into_iter()
            .map(|value| value.to_string())
            .collect::<Vec<_>>()
            .join("\n"),
    )
    .unwrap();

    let responses = call(&[
        serde_json::json!({ "jsonrpc": "2.0", "id": 1, "method": "initialize", "params": { "protocolVersion": "2024-11-05", "capabilities": {}, "clientInfo": { "name": "test", "version": "1" } } }),
        serde_json::json!({ "jsonrpc": "2.0", "method": "notifications/initialized", "params": {} }),
        serde_json::json!({ "jsonrpc": "2.0", "id": 2, "method": "tools/list", "params": {} }),
        serde_json::json!({ "jsonrpc": "2.0", "id": 3, "method": "tools/call", "params": { "name": "zuma_handoff", "arguments": { "operation": "prepare", "sessionPath": session.to_string_lossy(), "workingDirectory": root.to_string_lossy(), "nextActionHint": long_next_action, "maxChars": 1000 } } }),
    ]);

    let tools = responses
        .iter()
        .find(|response| response["id"] == 2)
        .unwrap()["result"]["tools"]
        .as_array()
        .unwrap();
    assert!(tools.iter().any(|tool| tool["name"] == "zuma_handoff"));
    let payload = tool_payload(&responses, 3);
    assert_eq!(payload["status"], "success");
    assert!(
        payload["handoffText"]
            .as_str()
            .unwrap()
            .contains("Implement safe handoff generation")
    );
    assert!(
        payload["handoffText"]
            .as_str()
            .unwrap()
            .contains("NEXT_ACTION\n- Next: update the Marketplace display name")
    );
    assert!(!payload["handoffText"].as_str().unwrap().contains("secret="));
    assert!(payload["sourceBytes"].as_u64().unwrap() > 0);
    assert!(payload["responseBytes"].as_u64().unwrap() > 0);
    assert!(payload["handoffText"].as_str().unwrap().chars().count() <= 1000);

    std::fs::remove_dir_all(root).unwrap();
}

#[test]
fn requires_explicit_handoff_selection_and_rejects_disabled_or_invalid_sessions() {
    let unique = SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .unwrap()
        .as_nanos();
    let root = std::env::temp_dir().join(format!("zuma-handoff-selection-{unique}"));
    let sessions = root.join("workspace/chatSessions");
    std::fs::create_dir_all(&sessions).unwrap();
    let seed = serde_json::json!({
        "kind": 0,
        "v": { "version": 3, "requests": [{ "message": { "text": "Goal" } }], "inputState": {} }
    });
    std::fs::write(sessions.join("one.jsonl"), seed.to_string()).unwrap();
    std::fs::write(sessions.join("two.jsonl"), seed.to_string()).unwrap();
    let invalid = root.join("invalid.jsonl");
    std::fs::write(&invalid, "not-json").unwrap();

    let common = [
        serde_json::json!({ "jsonrpc": "2.0", "id": 1, "method": "initialize", "params": { "protocolVersion": "2024-11-05", "capabilities": {}, "clientInfo": { "name": "test", "version": "1" } } }),
        serde_json::json!({ "jsonrpc": "2.0", "method": "notifications/initialized", "params": {} }),
    ];
    let candidate_request = serde_json::json!({ "jsonrpc": "2.0", "id": 2, "method": "tools/call", "params": { "name": "prepare_handoff", "arguments": {} } });
    let candidate_responses = call_with_environment(
        &[common[0].clone(), common[1].clone(), candidate_request],
        &[("ZUMA_CHAT_STORAGE_ROOT", root.to_str().unwrap())],
    );
    let candidates = tool_payload(&candidate_responses, 2);
    assert_eq!(candidates["status"], "candidates");
    assert_eq!(candidates["selectionRequired"], true);
    assert_eq!(candidates["candidates"].as_array().unwrap().len(), 2);
    assert!(candidates.get("handoffText").is_none());

    let disabled_request = serde_json::json!({ "jsonrpc": "2.0", "id": 3, "method": "tools/call", "params": { "name": "prepare_handoff", "arguments": { "sessionPath": invalid.to_string_lossy() } } });
    let disabled_responses = call_with_environment(
        &[common[0].clone(), common[1].clone(), disabled_request],
        &[("ZUMA_HANDOFF_ENABLED", "false")],
    );
    let disabled = tool_payload(&disabled_responses, 3);
    assert_eq!(disabled["status"], "error");
    assert!(disabled["message"].as_str().unwrap().contains("disabled"));

    let invalid_request = serde_json::json!({ "jsonrpc": "2.0", "id": 4, "method": "tools/call", "params": { "name": "prepare_handoff", "arguments": { "sessionPath": invalid.to_string_lossy() } } });
    let invalid_responses = call(&[common[0].clone(), common[1].clone(), invalid_request]);
    let invalid_payload = tool_payload(&invalid_responses, 4);
    assert_eq!(invalid_payload["status"], "error");
    assert!(
        invalid_payload["message"]
            .as_str()
            .unwrap()
            .contains("line 1")
    );

    std::fs::remove_dir_all(root).unwrap();
}
