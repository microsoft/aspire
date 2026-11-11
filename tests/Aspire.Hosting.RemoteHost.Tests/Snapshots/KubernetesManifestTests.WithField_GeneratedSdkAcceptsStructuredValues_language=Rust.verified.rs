//! aspire.rs - Capability-based Aspire SDK
//! GENERATED CODE - DO NOT EDIT

use std::collections::HashMap;
use std::sync::Arc;

use serde::{Deserialize, Serialize};
use serde_json::{json, Value};

use crate::transport::{
    AspireClient, CancellationToken, Handle,
    register_callback, register_cancellation, serialize_value,
};
use crate::base::{
    HandleWrapperBase, ResourceBuilderBase, ReferenceExpression,
    AspireList, AspireDict, serialize_handle, HasHandle,
};

// ============================================================================
// Handle Wrappers
// ============================================================================

/// Wrapper for Aspire.Hosting.Kubernetes/Aspire.Hosting.Kubernetes.KubernetesManifestResource
pub struct KubernetesManifestResource {
    handle: Handle,
    client: Arc<AspireClient>,
}

impl HasHandle for KubernetesManifestResource {
    fn handle(&self) -> &Handle {
        &self.handle
    }
}

impl KubernetesManifestResource {
    pub fn new(handle: Handle, client: Arc<AspireClient>) -> Self {
        Self { handle, client }
    }

    pub fn handle(&self) -> &Handle {
        &self.handle
    }

    pub fn client(&self) -> &Arc<AspireClient> {
        &self.client
    }

    /// Adds or updates a manifest field using a dot-separated path.
    pub fn with_field(&self, path: &str, value: &Value) -> Result<KubernetesManifestResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("context".to_string(), self.handle.to_json());
        args.insert("path".to_string(), serde_json::to_value(&path).unwrap_or(Value::Null));
        args.insert("value".to_string(), serde_json::to_value(&value).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting.Kubernetes/withField", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(KubernetesManifestResource::new(handle, self.client.clone()))
    }
}

// ============================================================================
// Handle wrapper registrations
// ============================================================================

pub fn register_all_wrappers() {
    // Handle wrappers are created inline in generated code
    // This function is provided for API compatibility
}

// ============================================================================
// Connection Helpers
// ============================================================================

/// Establishes a connection to the AppHost server.
pub fn connect() -> Result<Arc<AspireClient>, Box<dyn std::error::Error>> {
    let socket_path = std::env::var("REMOTE_APP_HOST_SOCKET_PATH")
        .map_err(|_| "REMOTE_APP_HOST_SOCKET_PATH environment variable not set. Run this application using `aspire run`")?;
    let client = Arc::new(AspireClient::new(&socket_path));
    client.connect()?;
    let auth_token = std::env::var("ASPIRE_REMOTE_APPHOST_TOKEN")
        .map_err(|_| "ASPIRE_REMOTE_APPHOST_TOKEN environment variable not set. Run this application using `aspire run`")?;
    client.authenticate(&auth_token)?;
    Ok(client)
}

/// Creates a new distributed application builder.
pub fn create_builder(options: Option<CreateBuilderOptions>) -> Result<DistributedApplicationBuilder, Box<dyn std::error::Error>> {
    let client = connect()?;
    let mut resolved_options: HashMap<String, Value> = HashMap::new();
    if let Some(opts) = options {
        for (k, v) in opts.to_map() {
            resolved_options.insert(k, v);
        }
    }
    if !resolved_options.contains_key("args") {
        let args: Vec<String> = std::env::args().skip(1).collect();
        resolved_options.insert("args".to_string(), serde_json::to_value(args).unwrap_or(Value::Null));
    }
    if !resolved_options.contains_key("projectDirectory") {
        if let Some(project_directory) = std::env::var("ASPIRE_PROJECT_DIRECTORY").ok().filter(|s| !s.is_empty()) {
            resolved_options.insert("projectDirectory".to_string(), Value::String(project_directory));
        } else if let Ok(pwd) = std::env::current_dir() {
            resolved_options.insert("projectDirectory".to_string(), Value::String(pwd.to_string_lossy().to_string()));
        }
    }
    if !resolved_options.contains_key("appHostFilePath") {
        if let Ok(app_host_file_path) = std::env::var("ASPIRE_APPHOST_FILEPATH") {
            if !app_host_file_path.is_empty() {
                resolved_options.insert("appHostFilePath".to_string(), Value::String(app_host_file_path));
            }
        }
    }
    let mut args: HashMap<String, Value> = HashMap::new();
    args.insert("argsOrOptions".to_string(), serde_json::to_value(resolved_options).unwrap_or(Value::Null));
    let result = client.invoke_capability("Aspire.Hosting/createBuilder", args)?;
    let handle: Handle = serde_json::from_value(result)?;
    Ok(DistributedApplicationBuilder::new(handle, client))
}