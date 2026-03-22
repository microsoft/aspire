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
// Enums
// ============================================================================

/// TestPersistenceMode
#[derive(Debug, Clone, Copy, Default, PartialEq, Eq, Serialize, Deserialize)]
pub enum TestPersistenceMode {
    #[default]
    #[serde(rename = "None")]
    None,
    #[serde(rename = "Volume")]
    Volume,
    #[serde(rename = "Bind")]
    Bind,
}

impl std::fmt::Display for TestPersistenceMode {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        match self {
            Self::None => write!(f, "None"),
            Self::Volume => write!(f, "Volume"),
            Self::Bind => write!(f, "Bind"),
        }
    }
}

/// TestResourceStatus
#[derive(Debug, Clone, Copy, Default, PartialEq, Eq, Serialize, Deserialize)]
pub enum TestResourceStatus {
    #[default]
    #[serde(rename = "Pending")]
    Pending,
    #[serde(rename = "Running")]
    Running,
    #[serde(rename = "Stopped")]
    Stopped,
    #[serde(rename = "Failed")]
    Failed,
}

impl std::fmt::Display for TestResourceStatus {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        match self {
            Self::Pending => write!(f, "Pending"),
            Self::Running => write!(f, "Running"),
            Self::Stopped => write!(f, "Stopped"),
            Self::Failed => write!(f, "Failed"),
        }
    }
}

// ============================================================================
// DTOs
// ============================================================================

/// TestConfigDto
#[derive(Debug, Clone, Default, Serialize, Deserialize)]
pub struct TestConfigDto {
    #[serde(rename = "Name")]
    pub name: String,
    #[serde(rename = "Port")]
    pub port: f64,
    #[serde(rename = "Enabled")]
    pub enabled: bool,
    #[serde(rename = "OptionalField")]
    pub optional_field: String,
}

impl TestConfigDto {
    pub fn to_map(&self) -> HashMap<String, Value> {
        let mut map = HashMap::new();
        map.insert("Name".to_string(), serde_json::to_value(&self.name).unwrap_or(Value::Null));
        map.insert("Port".to_string(), serde_json::to_value(&self.port).unwrap_or(Value::Null));
        map.insert("Enabled".to_string(), serde_json::to_value(&self.enabled).unwrap_or(Value::Null));
        map.insert("OptionalField".to_string(), serde_json::to_value(&self.optional_field).unwrap_or(Value::Null));
        map
    }
}

/// TestNestedDto
#[derive(Debug, Clone, Default, Serialize, Deserialize)]
pub struct TestNestedDto {
    #[serde(rename = "Id")]
    pub id: String,
    #[serde(rename = "Config")]
    pub config: TestConfigDto,
    #[serde(rename = "Tags")]
    pub tags: Vec<String>,
    #[serde(rename = "Counts")]
    pub counts: HashMap<String, f64>,
}

impl TestNestedDto {
    pub fn to_map(&self) -> HashMap<String, Value> {
        let mut map = HashMap::new();
        map.insert("Id".to_string(), serde_json::to_value(&self.id).unwrap_or(Value::Null));
        map.insert("Config".to_string(), serde_json::to_value(&self.config).unwrap_or(Value::Null));
        map.insert("Tags".to_string(), serde_json::to_value(&self.tags).unwrap_or(Value::Null));
        map.insert("Counts".to_string(), serde_json::to_value(&self.counts).unwrap_or(Value::Null));
        map
    }
}

/// TestDeeplyNestedDto
#[derive(Debug, Clone, Default, Serialize, Deserialize)]
pub struct TestDeeplyNestedDto {
    #[serde(rename = "NestedData")]
    pub nested_data: HashMap<String, Vec<TestConfigDto>>,
    #[serde(rename = "MetadataArray")]
    pub metadata_array: Vec<HashMap<String, String>>,
}

impl TestDeeplyNestedDto {
    pub fn to_map(&self) -> HashMap<String, Value> {
        let mut map = HashMap::new();
        map.insert("NestedData".to_string(), serde_json::to_value(&self.nested_data).unwrap_or(Value::Null));
        map.insert("MetadataArray".to_string(), serde_json::to_value(&self.metadata_array).unwrap_or(Value::Null));
        map
    }
}

// ============================================================================
// Handle Wrappers
// ============================================================================

/// Wrapper for Aspire.Hosting/Aspire.Hosting.IDistributedApplicationBuilder
pub struct IDistributedApplicationBuilder {
    handle: Handle,
    client: Arc<AspireClient>,
}

impl HasHandle for IDistributedApplicationBuilder {
    fn handle(&self) -> &Handle {
        &self.handle
    }
}

impl IDistributedApplicationBuilder {
    pub fn new(handle: Handle, client: Arc<AspireClient>) -> Self {
        Self { handle, client }
    }

    pub fn handle(&self) -> &Handle {
        &self.handle
    }

    pub fn client(&self) -> &Arc<AspireClient> {
        &self.client
    }

    /// Adds a test Redis resource
    pub fn add_test_redis(&self, name: &str, port: Option<f64>) -> Result<TestRedisResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("name".to_string(), serde_json::to_value(&name).unwrap_or(Value::Null));
        if let Some(ref v) = port {
            args.insert("port".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        let result = self.client.invoke_capability("Aspire.Hosting.CodeGeneration.Rust.Tests/addTestRedis", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(TestRedisResource::new(handle, self.client.clone()))
    }

    /// Adds a test vault resource
    pub fn add_test_vault(&self, name: &str) -> Result<TestVaultResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("name".to_string(), serde_json::to_value(&name).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting.CodeGeneration.Rust.Tests/addTestVault", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(TestVaultResource::new(handle, self.client.clone()))
    }
}

/// Wrapper for Aspire.Hosting/Aspire.Hosting.ApplicationModel.IResource
pub struct IResource {
    handle: Handle,
    client: Arc<AspireClient>,
}

impl HasHandle for IResource {
    fn handle(&self) -> &Handle {
        &self.handle
    }
}

impl IResource {
    pub fn new(handle: Handle, client: Arc<AspireClient>) -> Self {
        Self { handle, client }
    }

    pub fn handle(&self) -> &Handle {
        &self.handle
    }

    pub fn client(&self) -> &Arc<AspireClient> {
        &self.client
    }
}

/// Wrapper for Aspire.Hosting/Aspire.Hosting.ApplicationModel.IResourceWithConnectionString
pub struct IResourceWithConnectionString {
    handle: Handle,
    client: Arc<AspireClient>,
}

impl HasHandle for IResourceWithConnectionString {
    fn handle(&self) -> &Handle {
        &self.handle
    }
}

impl IResourceWithConnectionString {
    pub fn new(handle: Handle, client: Arc<AspireClient>) -> Self {
        Self { handle, client }
    }

    pub fn handle(&self) -> &Handle {
        &self.handle
    }

    pub fn client(&self) -> &Arc<AspireClient> {
        &self.client
    }
}

/// Wrapper for Aspire.Hosting/Aspire.Hosting.ApplicationModel.IResourceWithEnvironment
pub struct IResourceWithEnvironment {
    handle: Handle,
    client: Arc<AspireClient>,
}

impl HasHandle for IResourceWithEnvironment {
    fn handle(&self) -> &Handle {
        &self.handle
    }
}

impl IResourceWithEnvironment {
    pub fn new(handle: Handle, client: Arc<AspireClient>) -> Self {
        Self { handle, client }
    }

    pub fn handle(&self) -> &Handle {
        &self.handle
    }

    pub fn client(&self) -> &Arc<AspireClient> {
        &self.client
    }
}

/// Wrapper for Aspire.Hosting.CodeGeneration.Rust.Tests/Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes.ITestVaultResource
pub struct ITestVaultResource {
    handle: Handle,
    client: Arc<AspireClient>,
}

impl HasHandle for ITestVaultResource {
    fn handle(&self) -> &Handle {
        &self.handle
    }
}

impl ITestVaultResource {
    pub fn new(handle: Handle, client: Arc<AspireClient>) -> Self {
        Self { handle, client }
    }

    pub fn handle(&self) -> &Handle {
        &self.handle
    }

    pub fn client(&self) -> &Arc<AspireClient> {
        &self.client
    }
}

/// Wrapper for Aspire.Hosting.CodeGeneration.Rust.Tests/Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes.TestCallbackContext
pub struct TestCallbackContext {
    handle: Handle,
    client: Arc<AspireClient>,
}

impl HasHandle for TestCallbackContext {
    fn handle(&self) -> &Handle {
        &self.handle
    }
}

impl TestCallbackContext {
    pub fn new(handle: Handle, client: Arc<AspireClient>) -> Self {
        Self { handle, client }
    }

    pub fn handle(&self) -> &Handle {
        &self.handle
    }

    pub fn client(&self) -> &Arc<AspireClient> {
        &self.client
    }

    /// Gets the Name property
    pub fn name(&self) -> Result<String, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("context".to_string(), self.handle.to_json());
        let result = self.client.invoke_capability("Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes/TestCallbackContext.name", args)?;
        Ok(serde_json::from_value(result)?)
    }

    /// Sets the Name property
    pub fn set_name(&self, value: &str) -> Result<TestCallbackContext, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("context".to_string(), self.handle.to_json());
        args.insert("value".to_string(), serde_json::to_value(&value).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes/TestCallbackContext.setName", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(TestCallbackContext::new(handle, self.client.clone()))
    }

    /// Gets the Value property
    pub fn value(&self) -> Result<f64, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("context".to_string(), self.handle.to_json());
        let result = self.client.invoke_capability("Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes/TestCallbackContext.value", args)?;
        Ok(serde_json::from_value(result)?)
    }

    /// Sets the Value property
    pub fn set_value(&self, value: f64) -> Result<TestCallbackContext, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("context".to_string(), self.handle.to_json());
        args.insert("value".to_string(), serde_json::to_value(&value).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes/TestCallbackContext.setValue", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(TestCallbackContext::new(handle, self.client.clone()))
    }

    /// Gets the CancellationToken property
    pub fn cancellation_token(&self) -> Result<CancellationToken, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("context".to_string(), self.handle.to_json());
        let result = self.client.invoke_capability("Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes/TestCallbackContext.cancellationToken", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(CancellationToken::new(handle, self.client.clone()))
    }

    /// Sets the CancellationToken property
    pub fn set_cancellation_token(&self, value: Option<&CancellationToken>) -> Result<TestCallbackContext, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("context".to_string(), self.handle.to_json());
        if let Some(token) = value {
            let token_id = register_cancellation(token, self.client.clone());
            args.insert("value".to_string(), Value::String(token_id));
        }
        let result = self.client.invoke_capability("Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes/TestCallbackContext.setCancellationToken", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(TestCallbackContext::new(handle, self.client.clone()))
    }
}

/// Wrapper for Aspire.Hosting.CodeGeneration.Rust.Tests/Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes.TestCollectionContext
pub struct TestCollectionContext {
    handle: Handle,
    client: Arc<AspireClient>,
}

impl HasHandle for TestCollectionContext {
    fn handle(&self) -> &Handle {
        &self.handle
    }
}

impl TestCollectionContext {
    pub fn new(handle: Handle, client: Arc<AspireClient>) -> Self {
        Self { handle, client }
    }

    pub fn handle(&self) -> &Handle {
        &self.handle
    }

    pub fn client(&self) -> &Arc<AspireClient> {
        &self.client
    }

    /// Gets the Items property
    pub fn items(&self) -> AspireList<String> {
        AspireList::with_getter(self.handle.clone(), self.client.clone(), "Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes/TestCollectionContext.items")
    }

    /// Gets the Metadata property
    pub fn metadata(&self) -> AspireDict<String, String> {
        AspireDict::with_getter(self.handle.clone(), self.client.clone(), "Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes/TestCollectionContext.metadata")
    }
}

/// Wrapper for Aspire.Hosting.CodeGeneration.Rust.Tests/Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes.TestDatabaseResource
pub struct TestDatabaseResource {
    handle: Handle,
    client: Arc<AspireClient>,
}

impl HasHandle for TestDatabaseResource {
    fn handle(&self) -> &Handle {
        &self.handle
    }
}

impl TestDatabaseResource {
    pub fn new(handle: Handle, client: Arc<AspireClient>) -> Self {
        Self { handle, client }
    }

    pub fn handle(&self) -> &Handle {
        &self.handle
    }

    pub fn client(&self) -> &Arc<AspireClient> {
        &self.client
    }

    /// Adds an optional string parameter
    pub fn with_optional_string(&self, value: Option<&str>, enabled: Option<bool>) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        if let Some(ref v) = value {
            args.insert("value".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        if let Some(ref v) = enabled {
            args.insert("enabled".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        let result = self.client.invoke_capability("Aspire.Hosting.CodeGeneration.Rust.Tests/withOptionalString", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Configures the resource with a DTO
    pub fn with_config(&self, config: TestConfigDto) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("config".to_string(), serde_json::to_value(&config).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting.CodeGeneration.Rust.Tests/withConfig", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Configures environment with callback (test version)
    pub fn test_with_environment_callback(&self, callback: impl Fn(Vec<Value>) -> Value + Send + Sync + 'static) -> Result<IResourceWithEnvironment, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        let callback_id = register_callback(callback);
        args.insert("callback".to_string(), Value::String(callback_id));
        let result = self.client.invoke_capability("Aspire.Hosting.CodeGeneration.Rust.Tests/testWithEnvironmentCallback", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResourceWithEnvironment::new(handle, self.client.clone()))
    }

    /// Sets the created timestamp
    pub fn with_created_at(&self, created_at: &str) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("createdAt".to_string(), serde_json::to_value(&created_at).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting.CodeGeneration.Rust.Tests/withCreatedAt", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Sets the modified timestamp
    pub fn with_modified_at(&self, modified_at: &str) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("modifiedAt".to_string(), serde_json::to_value(&modified_at).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting.CodeGeneration.Rust.Tests/withModifiedAt", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Sets the correlation ID
    pub fn with_correlation_id(&self, correlation_id: &str) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("correlationId".to_string(), serde_json::to_value(&correlation_id).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting.CodeGeneration.Rust.Tests/withCorrelationId", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Configures with optional callback
    pub fn with_optional_callback(&self, callback: impl Fn(Vec<Value>) -> Value + Send + Sync + 'static) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        let callback_id = register_callback(callback);
        args.insert("callback".to_string(), Value::String(callback_id));
        let result = self.client.invoke_capability("Aspire.Hosting.CodeGeneration.Rust.Tests/withOptionalCallback", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Sets the resource status
    pub fn with_status(&self, status: TestResourceStatus) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("status".to_string(), serde_json::to_value(&status).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting.CodeGeneration.Rust.Tests/withStatus", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Configures with nested DTO
    pub fn with_nested_config(&self, config: TestNestedDto) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("config".to_string(), serde_json::to_value(&config).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting.CodeGeneration.Rust.Tests/withNestedConfig", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Adds validation callback
    pub fn with_validator(&self, validator: impl Fn(Vec<Value>) -> Value + Send + Sync + 'static) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        let callback_id = register_callback(validator);
        args.insert("validator".to_string(), Value::String(callback_id));
        let result = self.client.invoke_capability("Aspire.Hosting.CodeGeneration.Rust.Tests/withValidator", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Waits for another resource (test version)
    pub fn test_wait_for(&self, dependency: &IResource) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("dependency".to_string(), dependency.handle().to_json());
        let result = self.client.invoke_capability("Aspire.Hosting.CodeGeneration.Rust.Tests/testWaitFor", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Adds a dependency on another resource
    pub fn with_dependency(&self, dependency: &IResourceWithConnectionString) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("dependency".to_string(), dependency.handle().to_json());
        let result = self.client.invoke_capability("Aspire.Hosting.CodeGeneration.Rust.Tests/withDependency", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Sets the endpoints
    pub fn with_endpoints(&self, endpoints: Vec<String>) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("endpoints".to_string(), serde_json::to_value(&endpoints).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting.CodeGeneration.Rust.Tests/withEndpoints", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Sets environment variables
    pub fn with_environment_variables(&self, variables: HashMap<String, String>) -> Result<IResourceWithEnvironment, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("variables".to_string(), serde_json::to_value(&variables).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting.CodeGeneration.Rust.Tests/withEnvironmentVariables", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResourceWithEnvironment::new(handle, self.client.clone()))
    }

    /// Performs a cancellable operation
    pub fn with_cancellable_operation(&self, operation: impl Fn(Vec<Value>) -> Value + Send + Sync + 'static) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        let callback_id = register_callback(operation);
        args.insert("operation".to_string(), Value::String(callback_id));
        let result = self.client.invoke_capability("Aspire.Hosting.CodeGeneration.Rust.Tests/withCancellableOperation", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Adds a data volume
    pub fn with_data_volume(&self, name: Option<&str>) -> Result<TestDatabaseResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        if let Some(ref v) = name {
            args.insert("name".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        let result = self.client.invoke_capability("Aspire.Hosting.CodeGeneration.Rust.Tests/withDataVolume", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(TestDatabaseResource::new(handle, self.client.clone()))
    }
}

/// Wrapper for Aspire.Hosting.CodeGeneration.Rust.Tests/Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes.TestEnvironmentContext
pub struct TestEnvironmentContext {
    handle: Handle,
    client: Arc<AspireClient>,
}

impl HasHandle for TestEnvironmentContext {
    fn handle(&self) -> &Handle {
        &self.handle
    }
}

impl TestEnvironmentContext {
    pub fn new(handle: Handle, client: Arc<AspireClient>) -> Self {
        Self { handle, client }
    }

    pub fn handle(&self) -> &Handle {
        &self.handle
    }

    pub fn client(&self) -> &Arc<AspireClient> {
        &self.client
    }

    /// Gets the Name property
    pub fn name(&self) -> Result<String, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("context".to_string(), self.handle.to_json());
        let result = self.client.invoke_capability("Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes/TestEnvironmentContext.name", args)?;
        Ok(serde_json::from_value(result)?)
    }

    /// Sets the Name property
    pub fn set_name(&self, value: &str) -> Result<TestEnvironmentContext, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("context".to_string(), self.handle.to_json());
        args.insert("value".to_string(), serde_json::to_value(&value).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes/TestEnvironmentContext.setName", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(TestEnvironmentContext::new(handle, self.client.clone()))
    }

    /// Gets the Description property
    pub fn description(&self) -> Result<String, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("context".to_string(), self.handle.to_json());
        let result = self.client.invoke_capability("Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes/TestEnvironmentContext.description", args)?;
        Ok(serde_json::from_value(result)?)
    }

    /// Sets the Description property
    pub fn set_description(&self, value: &str) -> Result<TestEnvironmentContext, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("context".to_string(), self.handle.to_json());
        args.insert("value".to_string(), serde_json::to_value(&value).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes/TestEnvironmentContext.setDescription", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(TestEnvironmentContext::new(handle, self.client.clone()))
    }

    /// Gets the Priority property
    pub fn priority(&self) -> Result<f64, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("context".to_string(), self.handle.to_json());
        let result = self.client.invoke_capability("Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes/TestEnvironmentContext.priority", args)?;
        Ok(serde_json::from_value(result)?)
    }

    /// Sets the Priority property
    pub fn set_priority(&self, value: f64) -> Result<TestEnvironmentContext, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("context".to_string(), self.handle.to_json());
        args.insert("value".to_string(), serde_json::to_value(&value).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes/TestEnvironmentContext.setPriority", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(TestEnvironmentContext::new(handle, self.client.clone()))
    }
}

/// Wrapper for Aspire.Hosting.CodeGeneration.Rust.Tests/Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes.TestRedisResource
pub struct TestRedisResource {
    handle: Handle,
    client: Arc<AspireClient>,
}

impl HasHandle for TestRedisResource {
    fn handle(&self) -> &Handle {
        &self.handle
    }
}

impl TestRedisResource {
    pub fn new(handle: Handle, client: Arc<AspireClient>) -> Self {
        Self { handle, client }
    }

    pub fn handle(&self) -> &Handle {
        &self.handle
    }

    pub fn client(&self) -> &Arc<AspireClient> {
        &self.client
    }

    /// Adds a child database to a test Redis resource
    pub fn add_test_child_database(&self, name: &str, database_name: Option<&str>) -> Result<TestDatabaseResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("name".to_string(), serde_json::to_value(&name).unwrap_or(Value::Null));
        if let Some(ref v) = database_name {
            args.insert("databaseName".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        let result = self.client.invoke_capability("Aspire.Hosting.CodeGeneration.Rust.Tests/addTestChildDatabase", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(TestDatabaseResource::new(handle, self.client.clone()))
    }

    /// Configures the Redis resource with persistence
    pub fn with_persistence(&self, mode: Option<TestPersistenceMode>) -> Result<TestRedisResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        if let Some(ref v) = mode {
            args.insert("mode".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        let result = self.client.invoke_capability("Aspire.Hosting.CodeGeneration.Rust.Tests/withPersistence", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(TestRedisResource::new(handle, self.client.clone()))
    }

    /// Adds an optional string parameter
    pub fn with_optional_string(&self, value: Option<&str>, enabled: Option<bool>) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        if let Some(ref v) = value {
            args.insert("value".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        if let Some(ref v) = enabled {
            args.insert("enabled".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        let result = self.client.invoke_capability("Aspire.Hosting.CodeGeneration.Rust.Tests/withOptionalString", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Configures the resource with a DTO
    pub fn with_config(&self, config: TestConfigDto) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("config".to_string(), serde_json::to_value(&config).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting.CodeGeneration.Rust.Tests/withConfig", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Gets the tags for the resource
    pub fn get_tags(&self) -> AspireList<String> {
        AspireList::with_getter(self.handle.clone(), self.client.clone(), "Aspire.Hosting.CodeGeneration.Rust.Tests/getTags")
    }

    /// Gets the metadata for the resource
    pub fn get_metadata(&self) -> AspireDict<String, String> {
        AspireDict::with_getter(self.handle.clone(), self.client.clone(), "Aspire.Hosting.CodeGeneration.Rust.Tests/getMetadata")
    }

    /// Sets the connection string using a reference expression
    pub fn with_connection_string(&self, connection_string: ReferenceExpression) -> Result<IResourceWithConnectionString, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("connectionString".to_string(), serde_json::to_value(&connection_string).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting.CodeGeneration.Rust.Tests/withConnectionString", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResourceWithConnectionString::new(handle, self.client.clone()))
    }

    /// Configures environment with callback (test version)
    pub fn test_with_environment_callback(&self, callback: impl Fn(Vec<Value>) -> Value + Send + Sync + 'static) -> Result<IResourceWithEnvironment, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        let callback_id = register_callback(callback);
        args.insert("callback".to_string(), Value::String(callback_id));
        let result = self.client.invoke_capability("Aspire.Hosting.CodeGeneration.Rust.Tests/testWithEnvironmentCallback", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResourceWithEnvironment::new(handle, self.client.clone()))
    }

    /// Sets the created timestamp
    pub fn with_created_at(&self, created_at: &str) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("createdAt".to_string(), serde_json::to_value(&created_at).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting.CodeGeneration.Rust.Tests/withCreatedAt", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Sets the modified timestamp
    pub fn with_modified_at(&self, modified_at: &str) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("modifiedAt".to_string(), serde_json::to_value(&modified_at).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting.CodeGeneration.Rust.Tests/withModifiedAt", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Sets the correlation ID
    pub fn with_correlation_id(&self, correlation_id: &str) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("correlationId".to_string(), serde_json::to_value(&correlation_id).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting.CodeGeneration.Rust.Tests/withCorrelationId", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Configures with optional callback
    pub fn with_optional_callback(&self, callback: impl Fn(Vec<Value>) -> Value + Send + Sync + 'static) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        let callback_id = register_callback(callback);
        args.insert("callback".to_string(), Value::String(callback_id));
        let result = self.client.invoke_capability("Aspire.Hosting.CodeGeneration.Rust.Tests/withOptionalCallback", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Sets the resource status
    pub fn with_status(&self, status: TestResourceStatus) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("status".to_string(), serde_json::to_value(&status).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting.CodeGeneration.Rust.Tests/withStatus", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Configures with nested DTO
    pub fn with_nested_config(&self, config: TestNestedDto) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("config".to_string(), serde_json::to_value(&config).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting.CodeGeneration.Rust.Tests/withNestedConfig", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Adds validation callback
    pub fn with_validator(&self, validator: impl Fn(Vec<Value>) -> Value + Send + Sync + 'static) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        let callback_id = register_callback(validator);
        args.insert("validator".to_string(), Value::String(callback_id));
        let result = self.client.invoke_capability("Aspire.Hosting.CodeGeneration.Rust.Tests/withValidator", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Waits for another resource (test version)
    pub fn test_wait_for(&self, dependency: &IResource) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("dependency".to_string(), dependency.handle().to_json());
        let result = self.client.invoke_capability("Aspire.Hosting.CodeGeneration.Rust.Tests/testWaitFor", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Gets the endpoints
    pub fn get_endpoints(&self) -> Result<Vec<String>, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        let result = self.client.invoke_capability("Aspire.Hosting.CodeGeneration.Rust.Tests/getEndpoints", args)?;
        Ok(serde_json::from_value(result)?)
    }

    /// Sets connection string using direct interface target
    pub fn with_connection_string_direct(&self, connection_string: &str) -> Result<IResourceWithConnectionString, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("connectionString".to_string(), serde_json::to_value(&connection_string).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting.CodeGeneration.Rust.Tests/withConnectionStringDirect", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResourceWithConnectionString::new(handle, self.client.clone()))
    }

    /// Redis-specific configuration
    pub fn with_redis_specific(&self, option: &str) -> Result<TestRedisResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("option".to_string(), serde_json::to_value(&option).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting.CodeGeneration.Rust.Tests/withRedisSpecific", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(TestRedisResource::new(handle, self.client.clone()))
    }

    /// Adds a dependency on another resource
    pub fn with_dependency(&self, dependency: &IResourceWithConnectionString) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("dependency".to_string(), dependency.handle().to_json());
        let result = self.client.invoke_capability("Aspire.Hosting.CodeGeneration.Rust.Tests/withDependency", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Sets the endpoints
    pub fn with_endpoints(&self, endpoints: Vec<String>) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("endpoints".to_string(), serde_json::to_value(&endpoints).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting.CodeGeneration.Rust.Tests/withEndpoints", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Sets environment variables
    pub fn with_environment_variables(&self, variables: HashMap<String, String>) -> Result<IResourceWithEnvironment, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("variables".to_string(), serde_json::to_value(&variables).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting.CodeGeneration.Rust.Tests/withEnvironmentVariables", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResourceWithEnvironment::new(handle, self.client.clone()))
    }

    /// Gets the status of the resource asynchronously
    pub fn get_status_async(&self, cancellation_token: Option<&CancellationToken>) -> Result<String, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        if let Some(token) = cancellation_token {
            let token_id = register_cancellation(token, self.client.clone());
            args.insert("cancellationToken".to_string(), Value::String(token_id));
        }
        let result = self.client.invoke_capability("Aspire.Hosting.CodeGeneration.Rust.Tests/getStatusAsync", args)?;
        Ok(serde_json::from_value(result)?)
    }

    /// Performs a cancellable operation
    pub fn with_cancellable_operation(&self, operation: impl Fn(Vec<Value>) -> Value + Send + Sync + 'static) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        let callback_id = register_callback(operation);
        args.insert("operation".to_string(), Value::String(callback_id));
        let result = self.client.invoke_capability("Aspire.Hosting.CodeGeneration.Rust.Tests/withCancellableOperation", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Waits for the resource to be ready
    pub fn wait_for_ready_async(&self, timeout: f64, cancellation_token: Option<&CancellationToken>) -> Result<bool, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("timeout".to_string(), serde_json::to_value(&timeout).unwrap_or(Value::Null));
        if let Some(token) = cancellation_token {
            let token_id = register_cancellation(token, self.client.clone());
            args.insert("cancellationToken".to_string(), Value::String(token_id));
        }
        let result = self.client.invoke_capability("Aspire.Hosting.CodeGeneration.Rust.Tests/waitForReadyAsync", args)?;
        Ok(serde_json::from_value(result)?)
    }

    /// Tests multi-param callback destructuring
    pub fn with_multi_param_handle_callback(&self, callback: impl Fn(Vec<Value>) -> Value + Send + Sync + 'static) -> Result<TestRedisResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        let callback_id = register_callback(callback);
        args.insert("callback".to_string(), Value::String(callback_id));
        let result = self.client.invoke_capability("Aspire.Hosting.CodeGeneration.Rust.Tests/withMultiParamHandleCallback", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(TestRedisResource::new(handle, self.client.clone()))
    }

    /// Adds a data volume with persistence
    pub fn with_data_volume(&self, name: Option<&str>, is_read_only: Option<bool>) -> Result<TestRedisResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        if let Some(ref v) = name {
            args.insert("name".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        if let Some(ref v) = is_read_only {
            args.insert("isReadOnly".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        let result = self.client.invoke_capability("Aspire.Hosting.CodeGeneration.Rust.Tests/withDataVolume", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(TestRedisResource::new(handle, self.client.clone()))
    }
}

/// Wrapper for Aspire.Hosting.CodeGeneration.Rust.Tests/Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes.TestResourceContext
pub struct TestResourceContext {
    handle: Handle,
    client: Arc<AspireClient>,
}

impl HasHandle for TestResourceContext {
    fn handle(&self) -> &Handle {
        &self.handle
    }
}

impl TestResourceContext {
    pub fn new(handle: Handle, client: Arc<AspireClient>) -> Self {
        Self { handle, client }
    }

    pub fn handle(&self) -> &Handle {
        &self.handle
    }

    pub fn client(&self) -> &Arc<AspireClient> {
        &self.client
    }

    /// Gets the Name property
    pub fn name(&self) -> Result<String, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("context".to_string(), self.handle.to_json());
        let result = self.client.invoke_capability("Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes/TestResourceContext.name", args)?;
        Ok(serde_json::from_value(result)?)
    }

    /// Sets the Name property
    pub fn set_name(&self, value: &str) -> Result<TestResourceContext, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("context".to_string(), self.handle.to_json());
        args.insert("value".to_string(), serde_json::to_value(&value).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes/TestResourceContext.setName", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(TestResourceContext::new(handle, self.client.clone()))
    }

    /// Gets the Value property
    pub fn value(&self) -> Result<f64, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("context".to_string(), self.handle.to_json());
        let result = self.client.invoke_capability("Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes/TestResourceContext.value", args)?;
        Ok(serde_json::from_value(result)?)
    }

    /// Sets the Value property
    pub fn set_value(&self, value: f64) -> Result<TestResourceContext, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("context".to_string(), self.handle.to_json());
        args.insert("value".to_string(), serde_json::to_value(&value).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes/TestResourceContext.setValue", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(TestResourceContext::new(handle, self.client.clone()))
    }

    /// Invokes the GetValueAsync method
    pub fn get_value_async(&self) -> Result<String, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("context".to_string(), self.handle.to_json());
        let result = self.client.invoke_capability("Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes/TestResourceContext.getValueAsync", args)?;
        Ok(serde_json::from_value(result)?)
    }

    /// Invokes the SetValueAsync method
    pub fn set_value_async(&self, value: &str) -> Result<(), Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("context".to_string(), self.handle.to_json());
        args.insert("value".to_string(), serde_json::to_value(&value).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes/TestResourceContext.setValueAsync", args)?;
        Ok(())
    }

    /// Invokes the ValidateAsync method
    pub fn validate_async(&self) -> Result<bool, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("context".to_string(), self.handle.to_json());
        let result = self.client.invoke_capability("Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes/TestResourceContext.validateAsync", args)?;
        Ok(serde_json::from_value(result)?)
    }
}

/// Wrapper for Aspire.Hosting.CodeGeneration.Rust.Tests/Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes.TestVaultResource
pub struct TestVaultResource {
    handle: Handle,
    client: Arc<AspireClient>,
}

impl HasHandle for TestVaultResource {
    fn handle(&self) -> &Handle {
        &self.handle
    }
}

impl TestVaultResource {
    pub fn new(handle: Handle, client: Arc<AspireClient>) -> Self {
        Self { handle, client }
    }

    pub fn handle(&self) -> &Handle {
        &self.handle
    }

    pub fn client(&self) -> &Arc<AspireClient> {
        &self.client
    }

    /// Adds an optional string parameter
    pub fn with_optional_string(&self, value: Option<&str>, enabled: Option<bool>) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        if let Some(ref v) = value {
            args.insert("value".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        if let Some(ref v) = enabled {
            args.insert("enabled".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        let result = self.client.invoke_capability("Aspire.Hosting.CodeGeneration.Rust.Tests/withOptionalString", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Configures the resource with a DTO
    pub fn with_config(&self, config: TestConfigDto) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("config".to_string(), serde_json::to_value(&config).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting.CodeGeneration.Rust.Tests/withConfig", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Configures environment with callback (test version)
    pub fn test_with_environment_callback(&self, callback: impl Fn(Vec<Value>) -> Value + Send + Sync + 'static) -> Result<IResourceWithEnvironment, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        let callback_id = register_callback(callback);
        args.insert("callback".to_string(), Value::String(callback_id));
        let result = self.client.invoke_capability("Aspire.Hosting.CodeGeneration.Rust.Tests/testWithEnvironmentCallback", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResourceWithEnvironment::new(handle, self.client.clone()))
    }

    /// Sets the created timestamp
    pub fn with_created_at(&self, created_at: &str) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("createdAt".to_string(), serde_json::to_value(&created_at).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting.CodeGeneration.Rust.Tests/withCreatedAt", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Sets the modified timestamp
    pub fn with_modified_at(&self, modified_at: &str) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("modifiedAt".to_string(), serde_json::to_value(&modified_at).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting.CodeGeneration.Rust.Tests/withModifiedAt", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Sets the correlation ID
    pub fn with_correlation_id(&self, correlation_id: &str) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("correlationId".to_string(), serde_json::to_value(&correlation_id).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting.CodeGeneration.Rust.Tests/withCorrelationId", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Configures with optional callback
    pub fn with_optional_callback(&self, callback: impl Fn(Vec<Value>) -> Value + Send + Sync + 'static) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        let callback_id = register_callback(callback);
        args.insert("callback".to_string(), Value::String(callback_id));
        let result = self.client.invoke_capability("Aspire.Hosting.CodeGeneration.Rust.Tests/withOptionalCallback", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Sets the resource status
    pub fn with_status(&self, status: TestResourceStatus) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("status".to_string(), serde_json::to_value(&status).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting.CodeGeneration.Rust.Tests/withStatus", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Configures with nested DTO
    pub fn with_nested_config(&self, config: TestNestedDto) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("config".to_string(), serde_json::to_value(&config).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting.CodeGeneration.Rust.Tests/withNestedConfig", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Adds validation callback
    pub fn with_validator(&self, validator: impl Fn(Vec<Value>) -> Value + Send + Sync + 'static) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        let callback_id = register_callback(validator);
        args.insert("validator".to_string(), Value::String(callback_id));
        let result = self.client.invoke_capability("Aspire.Hosting.CodeGeneration.Rust.Tests/withValidator", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Waits for another resource (test version)
    pub fn test_wait_for(&self, dependency: &IResource) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("dependency".to_string(), dependency.handle().to_json());
        let result = self.client.invoke_capability("Aspire.Hosting.CodeGeneration.Rust.Tests/testWaitFor", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Adds a dependency on another resource
    pub fn with_dependency(&self, dependency: &IResourceWithConnectionString) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("dependency".to_string(), dependency.handle().to_json());
        let result = self.client.invoke_capability("Aspire.Hosting.CodeGeneration.Rust.Tests/withDependency", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Sets the endpoints
    pub fn with_endpoints(&self, endpoints: Vec<String>) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("endpoints".to_string(), serde_json::to_value(&endpoints).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting.CodeGeneration.Rust.Tests/withEndpoints", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Sets environment variables
    pub fn with_environment_variables(&self, variables: HashMap<String, String>) -> Result<IResourceWithEnvironment, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("variables".to_string(), serde_json::to_value(&variables).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting.CodeGeneration.Rust.Tests/withEnvironmentVariables", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResourceWithEnvironment::new(handle, self.client.clone()))
    }

    /// Performs a cancellable operation
    pub fn with_cancellable_operation(&self, operation: impl Fn(Vec<Value>) -> Value + Send + Sync + 'static) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        let callback_id = register_callback(operation);
        args.insert("operation".to_string(), Value::String(callback_id));
        let result = self.client.invoke_capability("Aspire.Hosting.CodeGeneration.Rust.Tests/withCancellableOperation", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Configures vault using direct interface target
    pub fn with_vault_direct(&self, option: &str) -> Result<ITestVaultResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("option".to_string(), serde_json::to_value(&option).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting.CodeGeneration.Rust.Tests/withVaultDirect", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(ITestVaultResource::new(handle, self.client.clone()))
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
    Ok(client)
}

/// Creates a new distributed application builder.
pub fn create_builder(options: Option<CreateBuilderOptions>) -> Result<IDistributedApplicationBuilder, Box<dyn std::error::Error>> {
    let client = connect()?;
    let mut resolved_options: HashMap<String, Value> = HashMap::new();
    if let Some(opts) = options {
        for (k, v) in opts.to_map() {
            resolved_options.insert(k, v);
        }
    }
    if !resolved_options.contains_key("Args") {
        let args: Vec<String> = std::env::args().skip(1).collect();
        resolved_options.insert("Args".to_string(), serde_json::to_value(args).unwrap_or(Value::Null));
    }
    if !resolved_options.contains_key("ProjectDirectory") {
        if let Ok(pwd) = std::env::current_dir() {
            resolved_options.insert("ProjectDirectory".to_string(), Value::String(pwd.to_string_lossy().to_string()));
        }
    }
    let mut args: HashMap<String, Value> = HashMap::new();
    args.insert("options".to_string(), serde_json::to_value(resolved_options).unwrap_or(Value::Null));
    let result = client.invoke_capability("Aspire.Hosting/createBuilderWithOptions", args)?;
    let handle: Handle = serde_json::from_value(result)?;
    Ok(IDistributedApplicationBuilder::new(handle, client))
}

