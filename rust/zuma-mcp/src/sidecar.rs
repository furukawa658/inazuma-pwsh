use serde_json::{Value, json};
use std::collections::HashMap;
use std::env;
use std::io::{BufRead, BufReader, Write};
use std::path::{Path, PathBuf};
use std::process::{Child, ChildStdin, Command, Stdio};
use std::sync::atomic::{AtomicU64, Ordering};
use std::sync::{Arc, Mutex, mpsc};
use std::time::Duration;

const REQUEST_TIMEOUT: Duration = Duration::from_secs(300);

pub struct Sidecar {
    input: Mutex<ChildStdin>,
    pending: Arc<Mutex<HashMap<String, mpsc::Sender<Value>>>>,
    active: Mutex<HashMap<String, String>>,
    next_id: AtomicU64,
    process: Mutex<Child>,
}

impl Sidecar {
    pub fn start() -> Result<Self, String> {
        Self::start_inner(true)
    }

    pub fn start_for_tasks() -> Result<Self, String> {
        Self::start_inner(false)
    }

    fn start_inner(initialize: bool) -> Result<Self, String> {
        let executable = resolve_executable()?;
        let mut child = Command::new(&executable)
            .arg("--parent-pid")
            .arg(std::process::id().to_string())
            .stdin(Stdio::piped())
            .stdout(Stdio::piped())
            .spawn()
            .map_err(|error| format!("Could not start C# compatibility server: {error}"))?;
        let input = child
            .stdin
            .take()
            .ok_or_else(|| "C# compatibility server stdin is unavailable".to_string())?;
        let stdout = child
            .stdout
            .take()
            .ok_or_else(|| "C# compatibility server stdout is unavailable".to_string())?;
        let pending = Arc::new(Mutex::new(HashMap::<String, mpsc::Sender<Value>>::new()));
        let reader_pending = Arc::clone(&pending);
        std::thread::spawn(move || {
            for line in BufReader::new(stdout).lines().map_while(Result::ok) {
                let Ok(message) = serde_json::from_str::<Value>(&line) else {
                    eprintln!("C# compatibility server returned invalid JSON-RPC output.");
                    continue;
                };
                let Some(id) = message.get("id").and_then(Value::as_str) else {
                    eprintln!("C# compatibility server response did not contain a string request ID.");
                    continue;
                };
                if let Some(sender) = reader_pending.lock().unwrap().remove(id) {
                    let _ = sender.send(message);
                } else {
                    eprintln!("C# compatibility server returned an unexpected request ID.");
                }
            }
            let waiting: Vec<_> = reader_pending.lock().unwrap().drain().collect();
            for (_, sender) in waiting {
                let _ = sender.send(json!({
                    "error": { "code": -32603, "message": "C# compatibility server exited" }
                }));
            }
        });

        let sidecar = Self {
            input: Mutex::new(input),
            pending,
            active: Mutex::new(HashMap::new()),
            next_id: AtomicU64::new(1),
            process: Mutex::new(child),
        };
        if initialize {
            sidecar.initialize()?;
        }
        Ok(sidecar)
    }

    fn initialize(&self) -> Result<(), String> {
        self.request(
            "initialize",
            json!({
                "protocolVersion": "2024-11-05",
                "capabilities": {},
                "clientInfo": { "name": "zuma-rust-router", "version": env!("CARGO_PKG_VERSION") }
            }),
        )?;
        self.notify("notifications/initialized", json!({}))?;
        Ok(())
    }

    pub fn request(&self, method: &str, params: Value) -> Result<Value, String> {
        self.request_for(None, method, params)
    }

    pub fn request_for(
        &self,
        client_id: Option<&Value>,
        method: &str,
        params: Value,
    ) -> Result<Value, String> {
        let id = format!("zuma-rust-{}", self.next_id.fetch_add(1, Ordering::Relaxed));
        let client_key = client_id.map(|value| serde_json::to_string(value).unwrap());
        let (sender, receiver) = mpsc::channel();
        self.pending.lock().unwrap().insert(id.clone(), sender);
        if let Some(key) = &client_key {
            self.active.lock().unwrap().insert(key.clone(), id.clone());
        }
        if let Err(error) =
            self.write(json!({ "jsonrpc": "2.0", "id": id, "method": method, "params": params }))
        {
            self.pending.lock().unwrap().remove(id.as_str());
            if let Some(key) = client_key {
                self.active.lock().unwrap().remove(&key);
            }
            return Err(error);
        }
        let response = receiver
            .recv_timeout(REQUEST_TIMEOUT)
            .map_err(|_| format!("C# compatibility server timed out for {method}"));
        if let Some(key) = client_key {
            self.active.lock().unwrap().remove(&key);
        }
        response
    }

    pub fn cancel(&self, params: &Value) -> Result<(), String> {
        let Some(request_id) = params.get("requestId") else {
            return Ok(());
        };
        let key = serde_json::to_string(request_id).unwrap();
        let Some(sidecar_id) = self.active.lock().unwrap().get(&key).cloned() else {
            return Ok(());
        };
        let mut forwarded = params.clone();
        forwarded["requestId"] = Value::String(sidecar_id);
        self.notify("notifications/cancelled", forwarded)
    }

    pub fn notify(&self, method: &str, params: Value) -> Result<(), String> {
        self.write(json!({ "jsonrpc": "2.0", "method": method, "params": params }))
    }

    fn write(&self, message: Value) -> Result<(), String> {
        let mut input = self.input.lock().unwrap();
        writeln!(input, "{}", serde_json::to_string(&message).unwrap())
            .map_err(|error| format!("Could not write to C# compatibility server: {error}"))?;
        input
            .flush()
            .map_err(|error| format!("Could not flush C# compatibility server: {error}"))
    }
}

impl Drop for Sidecar {
    fn drop(&mut self) {
        if let Ok(mut child) = self.process.lock() {
            let _ = child.kill();
            let _ = child.wait();
        }
    }
}

fn resolve_executable() -> Result<PathBuf, String> {
    if let Some(path) = env::var_os("ZUMA_CSHARP_SERVER") {
        let path = PathBuf::from(path);
        if path.is_file() {
            return Ok(path);
        }
        return Err(format!(
            "ZUMA_CSHARP_SERVER is not a file: {}",
            path.display()
        ));
    }

    let current = env::current_exe().map_err(|error| error.to_string())?;
    let directory = current.parent().unwrap_or_else(|| Path::new("."));
    let file_name = if cfg!(windows) {
        "InazumaPwsh.core.exe"
    } else {
        "InazumaPwsh.core"
    };
    let path = directory.join(file_name);
    if path.is_file() {
        Ok(path)
    } else {
        Err(format!(
            "C# compatibility server not found: {}",
            path.display()
        ))
    }
}
