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

    let initialize_response = responses
        .iter()
        .find(|response| response["id"] == 1)
        .expect("initialize response should be returned");
    assert_eq!(
        initialize_response["result"]["capabilities"]["extensions"]["io.modelcontextprotocol/tasks"],
        serde_json::json!({})
    );
    let list_response = responses
        .iter()
        .find(|response| response["id"] == 2)
        .expect("tools/list response should be returned");
    let tools = list_response["result"]["tools"].as_array().unwrap();
    assert_eq!(tools.len(), 22);
    assert!(
        tools
            .iter()
            .any(|tool| tool["name"] == "run_affected_tests")
    );
    for name in [
        "get_execution_gate",
        "set_execution_gate",
        "reset_execution_gate",
    ] {
        assert!(tools.iter().any(|tool| tool["name"] == name), "missing {name}");
    }
    assert!(tools.iter().any(|tool| tool["name"] == "run_cargo_check"));
    assert!(tools.iter().any(|tool| tool["name"] == "read_symbol"));
    for name in [
        "run_dotnet_build",
        "run_eslint",
        "run_tsc",
        "run_cargo_check",
    ] {
        let tool = tools.iter().find(|tool| tool["name"] == name).unwrap();
        assert_eq!(
            tool["inputSchema"]["properties"]["onlyNew"]["type"],
            "boolean"
        );
    }
    for name in [
        "run_dotnet_test",
        "run_playwright",
        "run_bun_test",
        "run_pytest",
        "run_vitest",
    ] {
        let tool = tools.iter().find(|tool| tool["name"] == name).unwrap();
        assert_eq!(
            tool["inputSchema"]["properties"]["regressionsOnly"]["type"],
            "boolean"
        );
    }
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
