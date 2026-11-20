use serde_json::Value;
use std::io::Write;
use std::process::{Command, Stdio};
use std::time::{SystemTime, UNIX_EPOCH};

fn call(requests: &[Value]) -> Vec<Value> {
    let mut child = Command::new(env!("CARGO_BIN_EXE_zuma-mcp"))
        .stdin(Stdio::piped())
        .stdout(Stdio::piped())
        .spawn()
        .expect("Rust MCP server should start");

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

    let list_response = responses
        .iter()
        .find(|response| response["id"] == 2)
        .expect("tools/list response should be returned");
    let tools = list_response["result"]["tools"].as_array().unwrap();
    assert_eq!(tools.len(), 17);
    assert!(tools.iter().any(|tool| tool["name"] == "run_affected_tests"));
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
    assert_eq!(responses[0]["result"]["serverInfo"]["name"], "Zuma");
    let payload: Value = serde_json::from_str(
        responses[1]["result"]["content"][0]["text"]
            .as_str()
            .unwrap(),
    )
    .unwrap();
    assert_eq!(
        payload["entries"],
        serde_json::json!(["1-4 1 First", "3-4 2 Second"])
    );
}
