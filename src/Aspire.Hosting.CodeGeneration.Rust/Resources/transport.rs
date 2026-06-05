//! Aspire ATS transport layer for JSON-RPC communication.

use std::collections::HashMap;
use std::io::{BufRead, BufReader, Read, Write};
use std::sync::atomic::{AtomicBool, AtomicU64, Ordering};
use std::sync::{Arc, Mutex, RwLock};

use serde::{Deserialize, Serialize};
use serde_json::{json, Value};

// Platform-specific connection type
#[cfg(target_os = "windows")]
type Connection = std::fs::File;
#[cfg(not(target_os = "windows"))]
type Connection = std::os::unix::net::UnixStream;

/// Standard ATS error codes.
pub mod ats_error_codes {
    pub const CAPABILITY_NOT_FOUND: &str = "CAPABILITY_NOT_FOUND";
    pub const HANDLE_NOT_FOUND: &str = "HANDLE_NOT_FOUND";
    pub const TYPE_MISMATCH: &str = "TYPE_MISMATCH";
    pub const INVALID_ARGUMENT: &str = "INVALID_ARGUMENT";
    pub const ARGUMENT_OUT_OF_RANGE: &str = "ARGUMENT_OUT_OF_RANGE";
    pub const CALLBACK_ERROR: &str = "CALLBACK_ERROR";
    pub const INTERNAL_ERROR: &str = "INTERNAL_ERROR";
}

/// Error returned from capability invocations.
#[derive(Debug, Clone)]
pub struct CapabilityError {
    pub code: String,
    pub message: String,
    pub capability: Option<String>,
}

impl std::fmt::Display for CapabilityError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        write!(f, "{}", self.message)
    }
}

impl std::error::Error for CapabilityError {}

/// A reference to a server-side object.
#[derive(Debug, Clone, Default, Serialize, Deserialize)]
pub struct Handle {
    #[serde(rename = "$handle")]
    pub handle_id: String,
    #[serde(rename = "$type")]
    pub type_id: String,
}

impl Handle {
    pub fn new(handle_id: String, type_id: String) -> Self {
        Self { handle_id, type_id }
    }

    pub fn to_json(&self) -> Value {
        json!({
            "$handle": self.handle_id,
            "$type": self.type_id
        })
    }
}

impl std::fmt::Display for Handle {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        write!(f, "Handle<{}>({})", self.type_id, self.handle_id)
    }
}

/// Checks if a value is a marshalled handle.
pub fn is_marshalled_handle(value: &Value) -> bool {
    if let Value::Object(obj) = value {
        obj.contains_key("$handle") && obj.contains_key("$type")
    } else {
        false
    }
}

/// Checks if a value is an ATS error.
pub fn is_ats_error(value: &Value) -> bool {
    if let Value::Object(obj) = value {
        obj.contains_key("$error")
    } else {
        false
    }
}

/// Type alias for handle wrapper factory functions.
pub type HandleWrapperFactory = Box<dyn Fn(Handle, Arc<AspireClient>) -> Box<dyn std::any::Any + Send + Sync> + Send + Sync>;

lazy_static::lazy_static! {
    static ref HANDLE_WRAPPER_REGISTRY: RwLock<HashMap<String, HandleWrapperFactory>> = RwLock::new(HashMap::new());
    static ref CALLBACK_REGISTRY: Mutex<HashMap<String, Box<dyn Fn(Vec<Value>) -> Value + Send + Sync>>> = Mutex::new(HashMap::new());
    static ref CALLBACK_COUNTER: AtomicU64 = AtomicU64::new(0);
}

/// Registers a handle wrapper factory for a type.
pub fn register_handle_wrapper(type_id: &str, factory: HandleWrapperFactory) {
    let mut registry = HANDLE_WRAPPER_REGISTRY.write().unwrap();
    registry.insert(type_id.to_string(), factory);
}

/// Wraps a value if it's a marshalled handle.
pub fn wrap_if_handle(value: Value, client: Option<Arc<AspireClient>>) -> Value {
    if !is_marshalled_handle(&value) {
        return value;
    }

    // For now, just return the value - handle wrapping will be done by generated code
    value
}

/// Registers a callback and returns its ID.
pub fn register_callback<F>(callback: F) -> String
where
    F: Fn(Vec<Value>) -> Value + Send + Sync + 'static,
{
    let id = format!(
        "callback_{}_{}",
        CALLBACK_COUNTER.fetch_add(1, Ordering::SeqCst),
        std::time::SystemTime::now()
            .duration_since(std::time::UNIX_EPOCH)
            .unwrap()
            .as_millis()
    );
    
    let mut registry = CALLBACK_REGISTRY.lock().unwrap();
    registry.insert(id.clone(), Box::new(callback));
    id
}

/// Unregisters a callback by ID.
pub fn unregister_callback(callback_id: &str) -> bool {
    let mut registry = CALLBACK_REGISTRY.lock().unwrap();
    registry.remove(callback_id).is_some()
}

/// Cancellation token for cooperative cancellation.
pub struct CancellationToken {
    handle: Option<Handle>,
    client: Option<Arc<AspireClient>>,
    cancelled: AtomicBool,
    callbacks: Mutex<Vec<Box<dyn FnOnce() + Send>>>,
}

impl CancellationToken {
    /// Create a new local cancellation token.
    pub fn new_local() -> Self {
        Self {
            handle: None,
            client: None,
            cancelled: AtomicBool::new(false),
            callbacks: Mutex::new(Vec::new()),
        }
    }

    /// Create a handle-backed cancellation token.
    pub fn new(handle: Handle, client: Arc<AspireClient>) -> Self {
        Self {
            handle: Some(handle),
            client: Some(client),
            cancelled: AtomicBool::new(false),
            callbacks: Mutex::new(Vec::new()),
        }
    }

    /// Get the handle if this is a handle-backed token.
    pub fn handle(&self) -> Option<&Handle> {
        self.handle.as_ref()
    }

    pub fn cancel(&self) {
        if self.cancelled.swap(true, Ordering::SeqCst) {
            return;
        }
        let callbacks: Vec<_> = {
            let mut guard = self.callbacks.lock().unwrap();
            std::mem::take(&mut *guard)
        };
        for cb in callbacks {
            cb();
        }
    }

    pub fn is_cancelled(&self) -> bool {
        self.cancelled.load(Ordering::SeqCst)
    }

    pub fn register<F>(&self, callback: F)
    where
        F: FnOnce() + Send + 'static,
    {
        if self.is_cancelled() {
            callback();
            return;
        }
        let mut guard = self.callbacks.lock().unwrap();
        guard.push(Box::new(callback));
    }
}

impl Default for CancellationToken {
    fn default() -> Self {
        Self::new_local()
    }
}

/// Registers a cancellation token with the client.
pub fn register_cancellation(token: &CancellationToken, client: Arc<AspireClient>) -> String {
    let id = format!(
        "ct_{}_{}",
        std::time::SystemTime::now()
            .duration_since(std::time::UNIX_EPOCH)
            .unwrap()
            .as_millis(),
        std::time::SystemTime::now()
            .duration_since(std::time::UNIX_EPOCH)
            .unwrap()
            .as_nanos()
    );
    
    let id_clone = id.clone();
    let client_clone = client;
    token.register(move || {
        let _ = client_clone.cancel_token(&id_clone);
    });
    
    id
}

/// Client for communicating with the AppHost server.
pub struct AspireClient {
    socket_path: String,
    conn: Mutex<Option<Connection>>,
    next_id: AtomicU64,
    connected: AtomicBool,
    disconnect_callbacks: Mutex<Vec<Box<dyn Fn() + Send + Sync>>>,
}

impl AspireClient {
    pub fn new(socket_path: &str) -> Self {
        Self {
            socket_path: socket_path.to_string(),
            conn: Mutex::new(None),
            next_id: AtomicU64::new(1),
            connected: AtomicBool::new(false),
            disconnect_callbacks: Mutex::new(Vec::new()),
        }
    }

    /// Connects to the AppHost server.
    pub fn connect(&self) -> Result<(), Box<dyn std::error::Error>> {
        if self.connected.load(Ordering::SeqCst) {
            return Ok(());
        }

        let conn = open_connection(&self.socket_path)?;
        *self.conn.lock().unwrap() = Some(conn);
        self.connected.store(true, Ordering::SeqCst);
        
        eprintln!("[Rust ATS] Connected to AppHost server");
        Ok(())
    }

    /// Registers a callback for disconnection.
    pub fn on_disconnect<F>(&self, callback: F)
    where
        F: Fn() + Send + Sync + 'static,
    {
        let mut callbacks = self.disconnect_callbacks.lock().unwrap();
        callbacks.push(Box::new(callback));
    }

    /// Invokes a capability on the server.
    pub fn invoke_capability(
        &self,
        capability_id: &str,
        args: HashMap<String, Value>,
    ) -> Result<Value, Box<dyn std::error::Error>> {
        let result = self.send_request("invokeCapability", json!([capability_id, args]))?;
        
        if is_ats_error(&result) {
            if let Value::Object(obj) = &result {
                if let Some(Value::Object(err_obj)) = obj.get("$error") {
                    return Err(Box::new(CapabilityError {
                        code: err_obj
                            .get("code")
                            .and_then(|v| v.as_str())
                            .unwrap_or("")
                            .to_string(),
                        message: err_obj
                            .get("message")
                            .and_then(|v| v.as_str())
                            .unwrap_or("")
                            .to_string(),
                        capability: err_obj
                            .get("capability")
                            .and_then(|v| v.as_str())
                            .map(|s| s.to_string()),
                    }));
                }
            }
        }
        
        Ok(wrap_if_handle(result, None))
    }

    /// Authenticates the client session with the AppHost server.
    pub fn authenticate(&self, token: &str) -> Result<(), Box<dyn std::error::Error>> {
        let result = self.send_request("authenticate", json!([token]))?;
        if result.as_bool().unwrap_or(false) {
            return Ok(());
        }

        Err("Failed to authenticate to the AppHost server.".into())
    }

    /// Cancels a cancellation token on the server.
    pub fn cancel_token(&self, token_id: &str) -> Result<bool, Box<dyn std::error::Error>> {
        let result = self.send_request("cancelToken", json!([token_id]))?;
        Ok(result.as_bool().unwrap_or(false))
    }

    /// Disconnects from the server.
    pub fn disconnect(&self) {
        self.connected.store(false, Ordering::SeqCst);
        *self.conn.lock().unwrap() = None;
        
        let callbacks = self.disconnect_callbacks.lock().unwrap();
        for cb in callbacks.iter() {
            cb();
        }
    }

    fn send_request(&self, method: &str, params: Value) -> Result<Value, Box<dyn std::error::Error>> {
        let request_id = self.next_id.fetch_add(1, Ordering::SeqCst);
        
        let message = json!({
            "jsonrpc": "2.0",
            "id": request_id,
            "method": method,
            "params": params
        });

        eprintln!("[Rust ATS] Sending request {} with id={}", method, request_id);
        self.write_message(&message)?;

        loop {
            let response = self.read_message()?;
            eprintln!("[Rust ATS] Received response: {:?}", response);

            // Check if this is a callback request from the server
            if response.get("method").is_some() {
                self.handle_callback_request(&response)?;
                continue;
            }

            // Check if this is our response
            if let Some(resp_id) = response.get("id").and_then(|v| v.as_u64()) {
                if resp_id == request_id {
                    if let Some(error) = response.get("error") {
                        let message = error
                            .get("message")
                            .and_then(|v| v.as_str())
                            .unwrap_or("Unknown error");
                        return Err(message.into());
                    }
                    return Ok(response.get("result").cloned().unwrap_or(Value::Null));
                }
            }
        }
    }

    fn write_message(&self, message: &Value) -> Result<(), Box<dyn std::error::Error>> {
        let mut conn = self.conn.lock().unwrap();
        let conn = conn.as_mut().ok_or("Not connected to AppHost")?;
        
        let body = serde_json::to_string(message)?;
        let header = format!("Content-Length: {}\r\n\r\n", body.len());
        
        conn.write_all(header.as_bytes())?;
        conn.write_all(body.as_bytes())?;
        conn.flush()?;
        
        Ok(())
    }

    fn read_message(&self) -> Result<Value, Box<dyn std::error::Error>> {
        let mut conn = self.conn.lock().unwrap();
        let conn = conn.as_mut().ok_or("Not connected")?;
        
        // Read headers
        let mut headers = HashMap::new();
        let mut reader = BufReader::new(conn.try_clone()?);
        
        loop {
            let mut line = String::new();
            reader.read_line(&mut line)?;
            let line = line.trim();
            
            if line.is_empty() {
                break;
            }
            
            if let Some(idx) = line.find(':') {
                let key = line[..idx].trim().to_lowercase();
                let value = line[idx + 1..].trim().to_string();
                headers.insert(key, value);
            }
        }

        // Read body
        let content_length: usize = headers
            .get("content-length")
            .ok_or("Missing content-length")?
            .parse()?;
        
        let mut body = vec![0u8; content_length];
        reader.read_exact(&mut body)?;
        
        let message: Value = serde_json::from_slice(&body)?;
        Ok(message)
    }

    fn handle_callback_request(&self, message: &Value) -> Result<(), Box<dyn std::error::Error>> {
        let method = message
            .get("method")
            .and_then(|v| v.as_str())
            .unwrap_or("");
        let request_id = message.get("id").cloned();

        if method != "invokeCallback" {
            if let Some(id) = request_id {
                self.write_message(&json!({
                    "jsonrpc": "2.0",
                    "id": id,
                    "error": {"code": -32601, "message": format!("Unknown method: {}", method)}
                }))?;
            }
            return Ok(());
        }

        let params = message.get("params").and_then(|v| v.as_array());
        let callback_id = params
            .and_then(|p| p.first())
            .and_then(|v| v.as_str())
            .unwrap_or("");
        let args = params.and_then(|p| p.get(1)).cloned().unwrap_or(Value::Null);

        let result = invoke_callback(callback_id, &args);
        
        match result {
            Ok(value) => {
                if let Some(id) = request_id {
                    self.write_message(&json!({
                        "jsonrpc": "2.0",
                        "id": id,
                        "result": value
                    }))?;
                }
            }
            Err(e) => {
                if let Some(id) = request_id {
                    self.write_message(&json!({
                        "jsonrpc": "2.0",
                        "id": id,
                        "error": {"code": -32000, "message": e.to_string()}
                    }))?;
                }
            }
        }
        
        Ok(())
    }
}

fn invoke_callback(callback_id: &str, args: &Value) -> Result<Value, Box<dyn std::error::Error>> {
    if callback_id.is_empty() {
        return Err("Callback ID missing".into());
    }

    let registry = CALLBACK_REGISTRY.lock().unwrap();
    let callback = registry
        .get(callback_id)
        .ok_or_else(|| format!("Callback not found: {}", callback_id))?;

    // Convert args to positional arguments
    let positional_args: Vec<Value> = if let Value::Object(obj) = args {
        let mut result = Vec::new();
        for i in 0.. {
            let key = format!("p{}", i);
            if let Some(val) = obj.get(&key) {
                result.push(val.clone());
            } else {
                break;
            }
        }
        result
    } else if !args.is_null() {
        vec![args.clone()]
    } else {
        Vec::new()
    };

    Ok(callback(positional_args))
}

#[cfg(target_os = "windows")]
fn open_connection(socket_path: &str) -> Result<Connection, Box<dyn std::error::Error>> {
    use std::path::Path;
    
    // Extract just the filename from the socket path for the named pipe
    let pipe_name = Path::new(socket_path)
        .file_name()
        .and_then(|n| n.to_str())
        .unwrap_or(socket_path);
    let pipe_path = format!("\\\\.\\pipe\\{}", pipe_name);
    eprintln!("[Rust ATS] Opening Windows named pipe: {}", pipe_path);
    
    let file = std::fs::OpenOptions::new()
        .read(true)
        .write(true)
        .open(&pipe_path)?;
    
    eprintln!("[Rust ATS] Named pipe opened successfully");
    Ok(file)
}

#[cfg(not(target_os = "windows"))]
fn open_connection(socket_path: &str) -> Result<Connection, Box<dyn std::error::Error>> {
    use std::os::unix::net::UnixStream;
    
    eprintln!("[Rust ATS] Opening Unix domain socket: {}", socket_path);
    let stream = UnixStream::connect(socket_path)?;
    eprintln!("[Rust ATS] Unix domain socket opened successfully");
    Ok(stream)
}

/// Serializes a value to its JSON representation.
pub fn serialize_value(value: &Value) -> Value {
    value.clone()
}
