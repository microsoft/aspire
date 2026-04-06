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

/// ContainerLifetime
#[derive(Debug, Clone, Copy, Default, PartialEq, Eq, Serialize, Deserialize)]
pub enum ContainerLifetime {
    #[default]
    #[serde(rename = "Session")]
    Session,
    #[serde(rename = "Persistent")]
    Persistent,
}

impl std::fmt::Display for ContainerLifetime {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        match self {
            Self::Session => write!(f, "Session"),
            Self::Persistent => write!(f, "Persistent"),
        }
    }
}

/// ImagePullPolicy
#[derive(Debug, Clone, Copy, Default, PartialEq, Eq, Serialize, Deserialize)]
pub enum ImagePullPolicy {
    #[default]
    #[serde(rename = "Default")]
    Default,
    #[serde(rename = "Always")]
    Always,
    #[serde(rename = "Missing")]
    Missing,
    #[serde(rename = "Never")]
    Never,
}

impl std::fmt::Display for ImagePullPolicy {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        match self {
            Self::Default => write!(f, "Default"),
            Self::Always => write!(f, "Always"),
            Self::Missing => write!(f, "Missing"),
            Self::Never => write!(f, "Never"),
        }
    }
}

/// DistributedApplicationOperation
#[derive(Debug, Clone, Copy, Default, PartialEq, Eq, Serialize, Deserialize)]
pub enum DistributedApplicationOperation {
    #[default]
    #[serde(rename = "Run")]
    Run,
    #[serde(rename = "Publish")]
    Publish,
}

impl std::fmt::Display for DistributedApplicationOperation {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        match self {
            Self::Run => write!(f, "Run"),
            Self::Publish => write!(f, "Publish"),
        }
    }
}

/// OtlpProtocol
#[derive(Debug, Clone, Copy, Default, PartialEq, Eq, Serialize, Deserialize)]
pub enum OtlpProtocol {
    #[default]
    #[serde(rename = "Grpc")]
    Grpc,
    #[serde(rename = "HttpProtobuf")]
    HttpProtobuf,
    #[serde(rename = "HttpJson")]
    HttpJson,
}

impl std::fmt::Display for OtlpProtocol {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        match self {
            Self::Grpc => write!(f, "Grpc"),
            Self::HttpProtobuf => write!(f, "HttpProtobuf"),
            Self::HttpJson => write!(f, "HttpJson"),
        }
    }
}

/// ProtocolType
#[derive(Debug, Clone, Copy, Default, PartialEq, Eq, Serialize, Deserialize)]
pub enum ProtocolType {
    #[default]
    #[serde(rename = "IP")]
    IP,
    #[serde(rename = "IPv6HopByHopOptions")]
    IPv6HopByHopOptions,
    #[serde(rename = "Unspecified")]
    Unspecified,
    #[serde(rename = "Icmp")]
    Icmp,
    #[serde(rename = "Igmp")]
    Igmp,
    #[serde(rename = "Ggp")]
    Ggp,
    #[serde(rename = "IPv4")]
    IPv4,
    #[serde(rename = "Tcp")]
    Tcp,
    #[serde(rename = "Pup")]
    Pup,
    #[serde(rename = "Udp")]
    Udp,
    #[serde(rename = "Idp")]
    Idp,
    #[serde(rename = "IPv6")]
    IPv6,
    #[serde(rename = "IPv6RoutingHeader")]
    IPv6RoutingHeader,
    #[serde(rename = "IPv6FragmentHeader")]
    IPv6FragmentHeader,
    #[serde(rename = "IPSecEncapsulatingSecurityPayload")]
    IPSecEncapsulatingSecurityPayload,
    #[serde(rename = "IPSecAuthenticationHeader")]
    IPSecAuthenticationHeader,
    #[serde(rename = "IcmpV6")]
    IcmpV6,
    #[serde(rename = "IPv6NoNextHeader")]
    IPv6NoNextHeader,
    #[serde(rename = "IPv6DestinationOptions")]
    IPv6DestinationOptions,
    #[serde(rename = "ND")]
    ND,
    #[serde(rename = "Raw")]
    Raw,
    #[serde(rename = "Ipx")]
    Ipx,
    #[serde(rename = "Spx")]
    Spx,
    #[serde(rename = "SpxII")]
    SpxII,
    #[serde(rename = "Unknown")]
    Unknown,
}

impl std::fmt::Display for ProtocolType {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        match self {
            Self::IP => write!(f, "IP"),
            Self::IPv6HopByHopOptions => write!(f, "IPv6HopByHopOptions"),
            Self::Unspecified => write!(f, "Unspecified"),
            Self::Icmp => write!(f, "Icmp"),
            Self::Igmp => write!(f, "Igmp"),
            Self::Ggp => write!(f, "Ggp"),
            Self::IPv4 => write!(f, "IPv4"),
            Self::Tcp => write!(f, "Tcp"),
            Self::Pup => write!(f, "Pup"),
            Self::Udp => write!(f, "Udp"),
            Self::Idp => write!(f, "Idp"),
            Self::IPv6 => write!(f, "IPv6"),
            Self::IPv6RoutingHeader => write!(f, "IPv6RoutingHeader"),
            Self::IPv6FragmentHeader => write!(f, "IPv6FragmentHeader"),
            Self::IPSecEncapsulatingSecurityPayload => write!(f, "IPSecEncapsulatingSecurityPayload"),
            Self::IPSecAuthenticationHeader => write!(f, "IPSecAuthenticationHeader"),
            Self::IcmpV6 => write!(f, "IcmpV6"),
            Self::IPv6NoNextHeader => write!(f, "IPv6NoNextHeader"),
            Self::IPv6DestinationOptions => write!(f, "IPv6DestinationOptions"),
            Self::ND => write!(f, "ND"),
            Self::Raw => write!(f, "Raw"),
            Self::Ipx => write!(f, "Ipx"),
            Self::Spx => write!(f, "Spx"),
            Self::SpxII => write!(f, "SpxII"),
            Self::Unknown => write!(f, "Unknown"),
        }
    }
}

/// WaitBehavior
#[derive(Debug, Clone, Copy, Default, PartialEq, Eq, Serialize, Deserialize)]
pub enum WaitBehavior {
    #[default]
    #[serde(rename = "WaitOnResourceUnavailable")]
    WaitOnResourceUnavailable,
    #[serde(rename = "StopOnResourceUnavailable")]
    StopOnResourceUnavailable,
}

impl std::fmt::Display for WaitBehavior {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        match self {
            Self::WaitOnResourceUnavailable => write!(f, "WaitOnResourceUnavailable"),
            Self::StopOnResourceUnavailable => write!(f, "StopOnResourceUnavailable"),
        }
    }
}

/// CertificateTrustScope
#[derive(Debug, Clone, Copy, Default, PartialEq, Eq, Serialize, Deserialize)]
pub enum CertificateTrustScope {
    #[default]
    #[serde(rename = "None")]
    None,
    #[serde(rename = "Append")]
    Append,
    #[serde(rename = "Override")]
    Override,
    #[serde(rename = "System")]
    System,
}

impl std::fmt::Display for CertificateTrustScope {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        match self {
            Self::None => write!(f, "None"),
            Self::Append => write!(f, "Append"),
            Self::Override => write!(f, "Override"),
            Self::System => write!(f, "System"),
        }
    }
}

/// IconVariant
#[derive(Debug, Clone, Copy, Default, PartialEq, Eq, Serialize, Deserialize)]
pub enum IconVariant {
    #[default]
    #[serde(rename = "Regular")]
    Regular,
    #[serde(rename = "Filled")]
    Filled,
}

impl std::fmt::Display for IconVariant {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        match self {
            Self::Regular => write!(f, "Regular"),
            Self::Filled => write!(f, "Filled"),
        }
    }
}

/// ProbeType
#[derive(Debug, Clone, Copy, Default, PartialEq, Eq, Serialize, Deserialize)]
pub enum ProbeType {
    #[default]
    #[serde(rename = "Startup")]
    Startup,
    #[serde(rename = "Readiness")]
    Readiness,
    #[serde(rename = "Liveness")]
    Liveness,
}

impl std::fmt::Display for ProbeType {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        match self {
            Self::Startup => write!(f, "Startup"),
            Self::Readiness => write!(f, "Readiness"),
            Self::Liveness => write!(f, "Liveness"),
        }
    }
}

/// EndpointProperty
#[derive(Debug, Clone, Copy, Default, PartialEq, Eq, Serialize, Deserialize)]
pub enum EndpointProperty {
    #[default]
    #[serde(rename = "Url")]
    Url,
    #[serde(rename = "Host")]
    Host,
    #[serde(rename = "IPV4Host")]
    IPV4Host,
    #[serde(rename = "Port")]
    Port,
    #[serde(rename = "Scheme")]
    Scheme,
    #[serde(rename = "TargetPort")]
    TargetPort,
    #[serde(rename = "HostAndPort")]
    HostAndPort,
    #[serde(rename = "TlsEnabled")]
    TlsEnabled,
}

impl std::fmt::Display for EndpointProperty {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        match self {
            Self::Url => write!(f, "Url"),
            Self::Host => write!(f, "Host"),
            Self::IPV4Host => write!(f, "IPV4Host"),
            Self::Port => write!(f, "Port"),
            Self::Scheme => write!(f, "Scheme"),
            Self::TargetPort => write!(f, "TargetPort"),
            Self::HostAndPort => write!(f, "HostAndPort"),
            Self::TlsEnabled => write!(f, "TlsEnabled"),
        }
    }
}

/// CommandResultFormat
#[derive(Debug, Clone, Copy, Default, PartialEq, Eq, Serialize, Deserialize)]
pub enum CommandResultFormat {
    #[default]
    #[serde(rename = "Text")]
    Text,
    #[serde(rename = "Json")]
    Json,
}

impl std::fmt::Display for CommandResultFormat {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        match self {
            Self::Text => write!(f, "Text"),
            Self::Json => write!(f, "Json"),
        }
    }
}

/// UrlDisplayLocation
#[derive(Debug, Clone, Copy, Default, PartialEq, Eq, Serialize, Deserialize)]
pub enum UrlDisplayLocation {
    #[default]
    #[serde(rename = "SummaryAndDetails")]
    SummaryAndDetails,
    #[serde(rename = "DetailsOnly")]
    DetailsOnly,
}

impl std::fmt::Display for UrlDisplayLocation {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        match self {
            Self::SummaryAndDetails => write!(f, "SummaryAndDetails"),
            Self::DetailsOnly => write!(f, "DetailsOnly"),
        }
    }
}

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

/// CreateBuilderOptions
#[derive(Debug, Clone, Default, Serialize, Deserialize)]
pub struct CreateBuilderOptions {
    #[serde(rename = "Args")]
    pub args: Vec<String>,
    #[serde(rename = "ProjectDirectory")]
    pub project_directory: String,
    #[serde(rename = "AppHostFilePath")]
    pub app_host_file_path: String,
    #[serde(rename = "ContainerRegistryOverride")]
    pub container_registry_override: String,
    #[serde(rename = "DisableDashboard")]
    pub disable_dashboard: bool,
    #[serde(rename = "DashboardApplicationName")]
    pub dashboard_application_name: String,
    #[serde(rename = "AllowUnsecuredTransport")]
    pub allow_unsecured_transport: bool,
    #[serde(rename = "EnableResourceLogging")]
    pub enable_resource_logging: bool,
}

impl CreateBuilderOptions {
    pub fn to_map(&self) -> HashMap<String, Value> {
        let mut map = HashMap::new();
        map.insert("Args".to_string(), serde_json::to_value(&self.args).unwrap_or(Value::Null));
        map.insert("ProjectDirectory".to_string(), serde_json::to_value(&self.project_directory).unwrap_or(Value::Null));
        map.insert("AppHostFilePath".to_string(), serde_json::to_value(&self.app_host_file_path).unwrap_or(Value::Null));
        map.insert("ContainerRegistryOverride".to_string(), serde_json::to_value(&self.container_registry_override).unwrap_or(Value::Null));
        map.insert("DisableDashboard".to_string(), serde_json::to_value(&self.disable_dashboard).unwrap_or(Value::Null));
        map.insert("DashboardApplicationName".to_string(), serde_json::to_value(&self.dashboard_application_name).unwrap_or(Value::Null));
        map.insert("AllowUnsecuredTransport".to_string(), serde_json::to_value(&self.allow_unsecured_transport).unwrap_or(Value::Null));
        map.insert("EnableResourceLogging".to_string(), serde_json::to_value(&self.enable_resource_logging).unwrap_or(Value::Null));
        map
    }
}

/// ResourceEventDto
#[derive(Debug, Clone, Default, Serialize, Deserialize)]
pub struct ResourceEventDto {
    #[serde(rename = "ResourceName")]
    pub resource_name: String,
    #[serde(rename = "ResourceId")]
    pub resource_id: String,
    #[serde(rename = "State")]
    pub state: String,
    #[serde(rename = "StateStyle")]
    pub state_style: String,
    #[serde(rename = "HealthStatus")]
    pub health_status: String,
    #[serde(rename = "ExitCode")]
    pub exit_code: f64,
}

impl ResourceEventDto {
    pub fn to_map(&self) -> HashMap<String, Value> {
        let mut map = HashMap::new();
        map.insert("ResourceName".to_string(), serde_json::to_value(&self.resource_name).unwrap_or(Value::Null));
        map.insert("ResourceId".to_string(), serde_json::to_value(&self.resource_id).unwrap_or(Value::Null));
        map.insert("State".to_string(), serde_json::to_value(&self.state).unwrap_or(Value::Null));
        map.insert("StateStyle".to_string(), serde_json::to_value(&self.state_style).unwrap_or(Value::Null));
        map.insert("HealthStatus".to_string(), serde_json::to_value(&self.health_status).unwrap_or(Value::Null));
        map.insert("ExitCode".to_string(), serde_json::to_value(&self.exit_code).unwrap_or(Value::Null));
        map
    }
}

/// CommandOptions
#[derive(Debug, Clone, Default, Serialize, Deserialize)]
pub struct CommandOptions {
    #[serde(rename = "Description")]
    pub description: String,
    #[serde(rename = "Parameter")]
    pub parameter: Value,
    #[serde(rename = "ConfirmationMessage")]
    pub confirmation_message: String,
    #[serde(rename = "IconName")]
    pub icon_name: String,
    #[serde(rename = "IconVariant")]
    pub icon_variant: IconVariant,
    #[serde(rename = "IsHighlighted")]
    pub is_highlighted: bool,
    #[serde(rename = "UpdateState")]
    pub update_state: Value,
}

impl CommandOptions {
    pub fn to_map(&self) -> HashMap<String, Value> {
        let mut map = HashMap::new();
        map.insert("Description".to_string(), serde_json::to_value(&self.description).unwrap_or(Value::Null));
        map.insert("Parameter".to_string(), serde_json::to_value(&self.parameter).unwrap_or(Value::Null));
        map.insert("ConfirmationMessage".to_string(), serde_json::to_value(&self.confirmation_message).unwrap_or(Value::Null));
        map.insert("IconName".to_string(), serde_json::to_value(&self.icon_name).unwrap_or(Value::Null));
        map.insert("IconVariant".to_string(), serde_json::to_value(&self.icon_variant).unwrap_or(Value::Null));
        map.insert("IsHighlighted".to_string(), serde_json::to_value(&self.is_highlighted).unwrap_or(Value::Null));
        map.insert("UpdateState".to_string(), serde_json::to_value(&self.update_state).unwrap_or(Value::Null));
        map
    }
}

/// ExecuteCommandResult
#[derive(Debug, Clone, Default, Serialize, Deserialize)]
pub struct ExecuteCommandResult {
    #[serde(rename = "Success")]
    pub success: bool,
    #[serde(rename = "Canceled")]
    pub canceled: bool,
    #[serde(rename = "ErrorMessage")]
    pub error_message: String,
    #[serde(rename = "Result")]
    pub result: String,
    #[serde(rename = "ResultFormat")]
    pub result_format: CommandResultFormat,
}

impl ExecuteCommandResult {
    pub fn to_map(&self) -> HashMap<String, Value> {
        let mut map = HashMap::new();
        map.insert("Success".to_string(), serde_json::to_value(&self.success).unwrap_or(Value::Null));
        map.insert("Canceled".to_string(), serde_json::to_value(&self.canceled).unwrap_or(Value::Null));
        map.insert("ErrorMessage".to_string(), serde_json::to_value(&self.error_message).unwrap_or(Value::Null));
        map.insert("Result".to_string(), serde_json::to_value(&self.result).unwrap_or(Value::Null));
        map.insert("ResultFormat".to_string(), serde_json::to_value(&self.result_format).unwrap_or(Value::Null));
        map
    }
}

/// ResourceUrlAnnotation
#[derive(Debug, Clone, Default, Serialize, Deserialize)]
pub struct ResourceUrlAnnotation {
    #[serde(rename = "Url")]
    pub url: String,
    #[serde(rename = "DisplayText")]
    pub display_text: String,
    #[serde(rename = "Endpoint")]
    pub endpoint: Handle,
    #[serde(rename = "DisplayLocation")]
    pub display_location: UrlDisplayLocation,
}

impl ResourceUrlAnnotation {
    pub fn to_map(&self) -> HashMap<String, Value> {
        let mut map = HashMap::new();
        map.insert("Url".to_string(), serde_json::to_value(&self.url).unwrap_or(Value::Null));
        map.insert("DisplayText".to_string(), serde_json::to_value(&self.display_text).unwrap_or(Value::Null));
        map.insert("Endpoint".to_string(), serde_json::to_value(&self.endpoint).unwrap_or(Value::Null));
        map.insert("DisplayLocation".to_string(), serde_json::to_value(&self.display_location).unwrap_or(Value::Null));
        map
    }
}

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

/// Wrapper for Aspire.Hosting/Aspire.Hosting.ApplicationModel.AfterResourcesCreatedEvent
pub struct AfterResourcesCreatedEvent {
    handle: Handle,
    client: Arc<AspireClient>,
}

impl HasHandle for AfterResourcesCreatedEvent {
    fn handle(&self) -> &Handle {
        &self.handle
    }
}

impl AfterResourcesCreatedEvent {
    pub fn new(handle: Handle, client: Arc<AspireClient>) -> Self {
        Self { handle, client }
    }

    pub fn handle(&self) -> &Handle {
        &self.handle
    }

    pub fn client(&self) -> &Arc<AspireClient> {
        &self.client
    }

    /// Gets the Services property
    pub fn services(&self) -> Result<IServiceProvider, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("context".to_string(), self.handle.to_json());
        let result = self.client.invoke_capability("Aspire.Hosting.ApplicationModel/AfterResourcesCreatedEvent.services", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IServiceProvider::new(handle, self.client.clone()))
    }

    /// Gets the Model property
    pub fn model(&self) -> Result<DistributedApplicationModel, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("context".to_string(), self.handle.to_json());
        let result = self.client.invoke_capability("Aspire.Hosting.ApplicationModel/AfterResourcesCreatedEvent.model", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(DistributedApplicationModel::new(handle, self.client.clone()))
    }
}

/// Wrapper for Aspire.Hosting/Aspire.Hosting.ApplicationModel.BeforeResourceStartedEvent
pub struct BeforeResourceStartedEvent {
    handle: Handle,
    client: Arc<AspireClient>,
}

impl HasHandle for BeforeResourceStartedEvent {
    fn handle(&self) -> &Handle {
        &self.handle
    }
}

impl BeforeResourceStartedEvent {
    pub fn new(handle: Handle, client: Arc<AspireClient>) -> Self {
        Self { handle, client }
    }

    pub fn handle(&self) -> &Handle {
        &self.handle
    }

    pub fn client(&self) -> &Arc<AspireClient> {
        &self.client
    }

    /// Gets the Resource property
    pub fn resource(&self) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("context".to_string(), self.handle.to_json());
        let result = self.client.invoke_capability("Aspire.Hosting.ApplicationModel/BeforeResourceStartedEvent.resource", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Gets the Services property
    pub fn services(&self) -> Result<IServiceProvider, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("context".to_string(), self.handle.to_json());
        let result = self.client.invoke_capability("Aspire.Hosting.ApplicationModel/BeforeResourceStartedEvent.services", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IServiceProvider::new(handle, self.client.clone()))
    }
}

/// Wrapper for Aspire.Hosting/Aspire.Hosting.ApplicationModel.BeforeStartEvent
pub struct BeforeStartEvent {
    handle: Handle,
    client: Arc<AspireClient>,
}

impl HasHandle for BeforeStartEvent {
    fn handle(&self) -> &Handle {
        &self.handle
    }
}

impl BeforeStartEvent {
    pub fn new(handle: Handle, client: Arc<AspireClient>) -> Self {
        Self { handle, client }
    }

    pub fn handle(&self) -> &Handle {
        &self.handle
    }

    pub fn client(&self) -> &Arc<AspireClient> {
        &self.client
    }

    /// Gets the Services property
    pub fn services(&self) -> Result<IServiceProvider, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("context".to_string(), self.handle.to_json());
        let result = self.client.invoke_capability("Aspire.Hosting.ApplicationModel/BeforeStartEvent.services", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IServiceProvider::new(handle, self.client.clone()))
    }

    /// Gets the Model property
    pub fn model(&self) -> Result<DistributedApplicationModel, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("context".to_string(), self.handle.to_json());
        let result = self.client.invoke_capability("Aspire.Hosting.ApplicationModel/BeforeStartEvent.model", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(DistributedApplicationModel::new(handle, self.client.clone()))
    }
}

/// Wrapper for Aspire.Hosting/Aspire.Hosting.ApplicationModel.CSharpAppResource
pub struct CSharpAppResource {
    handle: Handle,
    client: Arc<AspireClient>,
}

impl HasHandle for CSharpAppResource {
    fn handle(&self) -> &Handle {
        &self.handle
    }
}

impl CSharpAppResource {
    pub fn new(handle: Handle, client: Arc<AspireClient>) -> Self {
        Self { handle, client }
    }

    pub fn handle(&self) -> &Handle {
        &self.handle
    }

    pub fn client(&self) -> &Arc<AspireClient> {
        &self.client
    }

    /// Configures a resource to use a container registry
    pub fn with_container_registry(&self, registry: &IResource) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("registry".to_string(), registry.handle().to_json());
        let result = self.client.invoke_capability("Aspire.Hosting/withContainerRegistry", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Sets the base image for a Dockerfile build
    pub fn with_dockerfile_base_image(&self, build_image: Option<&str>, runtime_image: Option<&str>) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        if let Some(ref v) = build_image {
            args.insert("buildImage".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        if let Some(ref v) = runtime_image {
            args.insert("runtimeImage".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        let result = self.client.invoke_capability("Aspire.Hosting/withDockerfileBaseImage", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Configures an MCP server endpoint on the resource
    pub fn with_mcp_server(&self, path: Option<&str>, endpoint_name: Option<&str>) -> Result<IResourceWithEndpoints, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        if let Some(ref v) = path {
            args.insert("path".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        if let Some(ref v) = endpoint_name {
            args.insert("endpointName".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        let result = self.client.invoke_capability("Aspire.Hosting/withMcpServer", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResourceWithEndpoints::new(handle, self.client.clone()))
    }

    /// Configures OTLP telemetry export
    pub fn with_otlp_exporter(&self) -> Result<IResourceWithEnvironment, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        let result = self.client.invoke_capability("Aspire.Hosting/withOtlpExporter", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResourceWithEnvironment::new(handle, self.client.clone()))
    }

    /// Configures OTLP telemetry export with specific protocol
    pub fn with_otlp_exporter_protocol(&self, protocol: OtlpProtocol) -> Result<IResourceWithEnvironment, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("protocol".to_string(), serde_json::to_value(&protocol).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting/withOtlpExporterProtocol", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResourceWithEnvironment::new(handle, self.client.clone()))
    }

    /// Sets the number of replicas
    pub fn with_replicas(&self, replicas: f64) -> Result<ProjectResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("replicas".to_string(), serde_json::to_value(&replicas).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting/withReplicas", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(ProjectResource::new(handle, self.client.clone()))
    }

    /// Disables forwarded headers for the project
    pub fn disable_forwarded_headers(&self) -> Result<ProjectResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        let result = self.client.invoke_capability("Aspire.Hosting/disableForwardedHeaders", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(ProjectResource::new(handle, self.client.clone()))
    }

    /// Publishes a project as a Docker file with optional container configuration
    pub fn publish_as_docker_file(&self, configure: impl Fn(Vec<Value>) -> Value + Send + Sync + 'static) -> Result<ProjectResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        let callback_id = register_callback(configure);
        args.insert("configure".to_string(), Value::String(callback_id));
        let result = self.client.invoke_capability("Aspire.Hosting/publishProjectAsDockerFileWithConfigure", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(ProjectResource::new(handle, self.client.clone()))
    }

    /// Adds a required command dependency
    pub fn with_required_command(&self, command: &str, help_link: Option<&str>) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("command".to_string(), serde_json::to_value(&command).unwrap_or(Value::Null));
        if let Some(ref v) = help_link {
            args.insert("helpLink".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        let result = self.client.invoke_capability("Aspire.Hosting/withRequiredCommand", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Sets an environment variable
    pub fn with_environment(&self, name: &str, value: Value) -> Result<IResourceWithEnvironment, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("name".to_string(), serde_json::to_value(&name).unwrap_or(Value::Null));
        args.insert("value".to_string(), serde_json::to_value(&value).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting/withEnvironment", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResourceWithEnvironment::new(handle, self.client.clone()))
    }

    /// Sets an environment variable from a reference expression
    pub fn with_environment_expression(&self, name: &str, value: ReferenceExpression) -> Result<IResourceWithEnvironment, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("name".to_string(), serde_json::to_value(&name).unwrap_or(Value::Null));
        args.insert("value".to_string(), serde_json::to_value(&value).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting/withEnvironmentExpression", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResourceWithEnvironment::new(handle, self.client.clone()))
    }

    /// Sets environment variables via callback
    pub fn with_environment_callback(&self, callback: impl Fn(Vec<Value>) -> Value + Send + Sync + 'static) -> Result<IResourceWithEnvironment, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        let callback_id = register_callback(callback);
        args.insert("callback".to_string(), Value::String(callback_id));
        let result = self.client.invoke_capability("Aspire.Hosting/withEnvironmentCallback", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResourceWithEnvironment::new(handle, self.client.clone()))
    }

    /// Sets an environment variable from an endpoint reference
    pub fn with_environment_endpoint(&self, name: &str, endpoint_reference: &EndpointReference) -> Result<IResourceWithEnvironment, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("name".to_string(), serde_json::to_value(&name).unwrap_or(Value::Null));
        args.insert("endpointReference".to_string(), endpoint_reference.handle().to_json());
        let result = self.client.invoke_capability("Aspire.Hosting/withEnvironmentEndpoint", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResourceWithEnvironment::new(handle, self.client.clone()))
    }

    /// Sets an environment variable from a parameter resource
    pub fn with_environment_parameter(&self, name: &str, parameter: &ParameterResource) -> Result<IResourceWithEnvironment, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("name".to_string(), serde_json::to_value(&name).unwrap_or(Value::Null));
        args.insert("parameter".to_string(), parameter.handle().to_json());
        let result = self.client.invoke_capability("Aspire.Hosting/withEnvironmentParameter", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResourceWithEnvironment::new(handle, self.client.clone()))
    }

    /// Sets an environment variable from a connection string resource
    pub fn with_environment_connection_string(&self, env_var_name: &str, resource: &IResourceWithConnectionString) -> Result<IResourceWithEnvironment, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("envVarName".to_string(), serde_json::to_value(&env_var_name).unwrap_or(Value::Null));
        args.insert("resource".to_string(), resource.handle().to_json());
        let result = self.client.invoke_capability("Aspire.Hosting/withEnvironmentConnectionString", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResourceWithEnvironment::new(handle, self.client.clone()))
    }

    /// Adds arguments
    pub fn with_args(&self, args: Vec<String>) -> Result<IResourceWithArgs, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("args".to_string(), serde_json::to_value(&args).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting/withArgs", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResourceWithArgs::new(handle, self.client.clone()))
    }

    /// Sets command-line arguments via callback
    pub fn with_args_callback(&self, callback: impl Fn(Vec<Value>) -> Value + Send + Sync + 'static) -> Result<IResourceWithArgs, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        let callback_id = register_callback(callback);
        args.insert("callback".to_string(), Value::String(callback_id));
        let result = self.client.invoke_capability("Aspire.Hosting/withArgsCallback", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResourceWithArgs::new(handle, self.client.clone()))
    }

    /// Adds a reference to another resource
    pub fn with_reference(&self, source: &IResource, connection_name: Option<&str>, optional: Option<bool>, name: Option<&str>) -> Result<IResourceWithEnvironment, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("source".to_string(), source.handle().to_json());
        if let Some(ref v) = connection_name {
            args.insert("connectionName".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        if let Some(ref v) = optional {
            args.insert("optional".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        if let Some(ref v) = name {
            args.insert("name".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        let result = self.client.invoke_capability("Aspire.Hosting/withReference", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResourceWithEnvironment::new(handle, self.client.clone()))
    }

    /// Adds a reference to a URI
    pub fn with_reference_uri(&self, name: &str, uri: &str) -> Result<IResourceWithEnvironment, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("name".to_string(), serde_json::to_value(&name).unwrap_or(Value::Null));
        args.insert("uri".to_string(), serde_json::to_value(&uri).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting/withReferenceUri", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResourceWithEnvironment::new(handle, self.client.clone()))
    }

    /// Adds a reference to an external service
    pub fn with_reference_external_service(&self, external_service: &ExternalServiceResource) -> Result<IResourceWithEnvironment, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("externalService".to_string(), external_service.handle().to_json());
        let result = self.client.invoke_capability("Aspire.Hosting/withReferenceExternalService", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResourceWithEnvironment::new(handle, self.client.clone()))
    }

    /// Adds a reference to an endpoint
    pub fn with_reference_endpoint(&self, endpoint_reference: &EndpointReference) -> Result<IResourceWithEnvironment, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("endpointReference".to_string(), endpoint_reference.handle().to_json());
        let result = self.client.invoke_capability("Aspire.Hosting/withReferenceEndpoint", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResourceWithEnvironment::new(handle, self.client.clone()))
    }

    /// Adds a network endpoint
    pub fn with_endpoint(&self, port: Option<f64>, target_port: Option<f64>, scheme: Option<&str>, name: Option<&str>, env: Option<&str>, is_proxied: Option<bool>, is_external: Option<bool>, protocol: Option<ProtocolType>) -> Result<IResourceWithEndpoints, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        if let Some(ref v) = port {
            args.insert("port".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        if let Some(ref v) = target_port {
            args.insert("targetPort".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        if let Some(ref v) = scheme {
            args.insert("scheme".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        if let Some(ref v) = name {
            args.insert("name".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        if let Some(ref v) = env {
            args.insert("env".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        if let Some(ref v) = is_proxied {
            args.insert("isProxied".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        if let Some(ref v) = is_external {
            args.insert("isExternal".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        if let Some(ref v) = protocol {
            args.insert("protocol".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        let result = self.client.invoke_capability("Aspire.Hosting/withEndpoint", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResourceWithEndpoints::new(handle, self.client.clone()))
    }

    /// Adds an HTTP endpoint
    pub fn with_http_endpoint(&self, port: Option<f64>, target_port: Option<f64>, name: Option<&str>, env: Option<&str>, is_proxied: Option<bool>) -> Result<IResourceWithEndpoints, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        if let Some(ref v) = port {
            args.insert("port".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        if let Some(ref v) = target_port {
            args.insert("targetPort".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        if let Some(ref v) = name {
            args.insert("name".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        if let Some(ref v) = env {
            args.insert("env".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        if let Some(ref v) = is_proxied {
            args.insert("isProxied".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        let result = self.client.invoke_capability("Aspire.Hosting/withHttpEndpoint", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResourceWithEndpoints::new(handle, self.client.clone()))
    }

    /// Adds an HTTPS endpoint
    pub fn with_https_endpoint(&self, port: Option<f64>, target_port: Option<f64>, name: Option<&str>, env: Option<&str>, is_proxied: Option<bool>) -> Result<IResourceWithEndpoints, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        if let Some(ref v) = port {
            args.insert("port".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        if let Some(ref v) = target_port {
            args.insert("targetPort".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        if let Some(ref v) = name {
            args.insert("name".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        if let Some(ref v) = env {
            args.insert("env".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        if let Some(ref v) = is_proxied {
            args.insert("isProxied".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        let result = self.client.invoke_capability("Aspire.Hosting/withHttpsEndpoint", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResourceWithEndpoints::new(handle, self.client.clone()))
    }

    /// Makes HTTP endpoints externally accessible
    pub fn with_external_http_endpoints(&self) -> Result<IResourceWithEndpoints, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        let result = self.client.invoke_capability("Aspire.Hosting/withExternalHttpEndpoints", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResourceWithEndpoints::new(handle, self.client.clone()))
    }

    /// Gets an endpoint reference
    pub fn get_endpoint(&self, name: &str) -> Result<EndpointReference, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("name".to_string(), serde_json::to_value(&name).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting/getEndpoint", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(EndpointReference::new(handle, self.client.clone()))
    }

    /// Configures resource for HTTP/2
    pub fn as_http2_service(&self) -> Result<IResourceWithEndpoints, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        let result = self.client.invoke_capability("Aspire.Hosting/asHttp2Service", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResourceWithEndpoints::new(handle, self.client.clone()))
    }

    /// Customizes displayed URLs via callback
    pub fn with_urls_callback(&self, callback: impl Fn(Vec<Value>) -> Value + Send + Sync + 'static) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        let callback_id = register_callback(callback);
        args.insert("callback".to_string(), Value::String(callback_id));
        let result = self.client.invoke_capability("Aspire.Hosting/withUrlsCallback", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Adds or modifies displayed URLs
    pub fn with_url(&self, url: &str, display_text: Option<&str>) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("url".to_string(), serde_json::to_value(&url).unwrap_or(Value::Null));
        if let Some(ref v) = display_text {
            args.insert("displayText".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        let result = self.client.invoke_capability("Aspire.Hosting/withUrl", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Adds a URL using a reference expression
    pub fn with_url_expression(&self, url: ReferenceExpression, display_text: Option<&str>) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("url".to_string(), serde_json::to_value(&url).unwrap_or(Value::Null));
        if let Some(ref v) = display_text {
            args.insert("displayText".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        let result = self.client.invoke_capability("Aspire.Hosting/withUrlExpression", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Customizes the URL for a specific endpoint via callback
    pub fn with_url_for_endpoint(&self, endpoint_name: &str, callback: impl Fn(Vec<Value>) -> Value + Send + Sync + 'static) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("endpointName".to_string(), serde_json::to_value(&endpoint_name).unwrap_or(Value::Null));
        let callback_id = register_callback(callback);
        args.insert("callback".to_string(), Value::String(callback_id));
        let result = self.client.invoke_capability("Aspire.Hosting/withUrlForEndpoint", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Adds a URL for a specific endpoint via factory callback
    pub fn with_url_for_endpoint_factory(&self, endpoint_name: &str, callback: impl Fn(Vec<Value>) -> Value + Send + Sync + 'static) -> Result<IResourceWithEndpoints, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("endpointName".to_string(), serde_json::to_value(&endpoint_name).unwrap_or(Value::Null));
        let callback_id = register_callback(callback);
        args.insert("callback".to_string(), Value::String(callback_id));
        let result = self.client.invoke_capability("Aspire.Hosting/withUrlForEndpointFactory", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResourceWithEndpoints::new(handle, self.client.clone()))
    }

    /// Configures the resource to copy container files from the specified source during publishing
    pub fn publish_with_container_files(&self, source: &IResourceWithContainerFiles, destination_path: &str) -> Result<IContainerFilesDestinationResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("source".to_string(), source.handle().to_json());
        args.insert("destinationPath".to_string(), serde_json::to_value(&destination_path).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting/publishWithContainerFilesFromResource", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IContainerFilesDestinationResource::new(handle, self.client.clone()))
    }

    /// Excludes the resource from the deployment manifest
    pub fn exclude_from_manifest(&self) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        let result = self.client.invoke_capability("Aspire.Hosting/excludeFromManifest", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Waits for another resource to be ready
    pub fn wait_for(&self, dependency: &IResource) -> Result<IResourceWithWaitSupport, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("dependency".to_string(), dependency.handle().to_json());
        let result = self.client.invoke_capability("Aspire.Hosting/waitForResource", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResourceWithWaitSupport::new(handle, self.client.clone()))
    }

    /// Waits for another resource with specific behavior
    pub fn wait_for_with_behavior(&self, dependency: &IResource, wait_behavior: WaitBehavior) -> Result<IResourceWithWaitSupport, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("dependency".to_string(), dependency.handle().to_json());
        args.insert("waitBehavior".to_string(), serde_json::to_value(&wait_behavior).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting/waitForWithBehavior", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResourceWithWaitSupport::new(handle, self.client.clone()))
    }

    /// Waits for another resource to start
    pub fn wait_for_start(&self, dependency: &IResource) -> Result<IResourceWithWaitSupport, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("dependency".to_string(), dependency.handle().to_json());
        let result = self.client.invoke_capability("Aspire.Hosting/waitForResourceStart", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResourceWithWaitSupport::new(handle, self.client.clone()))
    }

    /// Waits for another resource to start with specific behavior
    pub fn wait_for_start_with_behavior(&self, dependency: &IResource, wait_behavior: WaitBehavior) -> Result<IResourceWithWaitSupport, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("dependency".to_string(), dependency.handle().to_json());
        args.insert("waitBehavior".to_string(), serde_json::to_value(&wait_behavior).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting/waitForStartWithBehavior", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResourceWithWaitSupport::new(handle, self.client.clone()))
    }

    /// Prevents resource from starting automatically
    pub fn with_explicit_start(&self) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        let result = self.client.invoke_capability("Aspire.Hosting/withExplicitStart", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Waits for resource completion
    pub fn wait_for_completion(&self, dependency: &IResource, exit_code: Option<f64>) -> Result<IResourceWithWaitSupport, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("dependency".to_string(), dependency.handle().to_json());
        if let Some(ref v) = exit_code {
            args.insert("exitCode".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        let result = self.client.invoke_capability("Aspire.Hosting/waitForResourceCompletion", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResourceWithWaitSupport::new(handle, self.client.clone()))
    }

    /// Adds a health check by key
    pub fn with_health_check(&self, key: &str) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("key".to_string(), serde_json::to_value(&key).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting/withHealthCheck", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Adds an HTTP health check
    pub fn with_http_health_check(&self, path: Option<&str>, status_code: Option<f64>, endpoint_name: Option<&str>) -> Result<IResourceWithEndpoints, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        if let Some(ref v) = path {
            args.insert("path".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        if let Some(ref v) = status_code {
            args.insert("statusCode".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        if let Some(ref v) = endpoint_name {
            args.insert("endpointName".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        let result = self.client.invoke_capability("Aspire.Hosting/withHttpHealthCheck", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResourceWithEndpoints::new(handle, self.client.clone()))
    }

    /// Adds a resource command
    pub fn with_command(&self, name: &str, display_name: &str, execute_command: impl Fn(Vec<Value>) -> Value + Send + Sync + 'static, command_options: Option<CommandOptions>) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("name".to_string(), serde_json::to_value(&name).unwrap_or(Value::Null));
        args.insert("displayName".to_string(), serde_json::to_value(&display_name).unwrap_or(Value::Null));
        let callback_id = register_callback(execute_command);
        args.insert("executeCommand".to_string(), Value::String(callback_id));
        if let Some(ref v) = command_options {
            args.insert("commandOptions".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        let result = self.client.invoke_capability("Aspire.Hosting/withCommand", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Configures developer certificate trust
    pub fn with_developer_certificate_trust(&self, trust: bool) -> Result<IResourceWithEnvironment, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("trust".to_string(), serde_json::to_value(&trust).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting/withDeveloperCertificateTrust", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResourceWithEnvironment::new(handle, self.client.clone()))
    }

    /// Sets the certificate trust scope
    pub fn with_certificate_trust_scope(&self, scope: CertificateTrustScope) -> Result<IResourceWithEnvironment, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("scope".to_string(), serde_json::to_value(&scope).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting/withCertificateTrustScope", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResourceWithEnvironment::new(handle, self.client.clone()))
    }

    /// Configures HTTPS with a developer certificate
    pub fn with_https_developer_certificate(&self, password: Option<&ParameterResource>) -> Result<IResourceWithEnvironment, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        if let Some(ref v) = password {
            args.insert("password".to_string(), v.handle().to_json());
        }
        let result = self.client.invoke_capability("Aspire.Hosting/withParameterHttpsDeveloperCertificate", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResourceWithEnvironment::new(handle, self.client.clone()))
    }

    /// Removes HTTPS certificate configuration
    pub fn without_https_certificate(&self) -> Result<IResourceWithEnvironment, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        let result = self.client.invoke_capability("Aspire.Hosting/withoutHttpsCertificate", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResourceWithEnvironment::new(handle, self.client.clone()))
    }

    /// Sets the parent relationship
    pub fn with_parent_relationship(&self, parent: &IResource) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("parent".to_string(), parent.handle().to_json());
        let result = self.client.invoke_capability("Aspire.Hosting/withBuilderParentRelationship", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Sets a child relationship
    pub fn with_child_relationship(&self, child: &IResource) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("child".to_string(), child.handle().to_json());
        let result = self.client.invoke_capability("Aspire.Hosting/withBuilderChildRelationship", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Sets the icon for the resource
    pub fn with_icon_name(&self, icon_name: &str, icon_variant: Option<IconVariant>) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("iconName".to_string(), serde_json::to_value(&icon_name).unwrap_or(Value::Null));
        if let Some(ref v) = icon_variant {
            args.insert("iconVariant".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        let result = self.client.invoke_capability("Aspire.Hosting/withIconName", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Adds an HTTP health probe to the resource
    pub fn with_http_probe(&self, probe_type: ProbeType, path: Option<&str>, initial_delay_seconds: Option<f64>, period_seconds: Option<f64>, timeout_seconds: Option<f64>, failure_threshold: Option<f64>, success_threshold: Option<f64>, endpoint_name: Option<&str>) -> Result<IResourceWithEndpoints, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("probeType".to_string(), serde_json::to_value(&probe_type).unwrap_or(Value::Null));
        if let Some(ref v) = path {
            args.insert("path".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        if let Some(ref v) = initial_delay_seconds {
            args.insert("initialDelaySeconds".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        if let Some(ref v) = period_seconds {
            args.insert("periodSeconds".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        if let Some(ref v) = timeout_seconds {
            args.insert("timeoutSeconds".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        if let Some(ref v) = failure_threshold {
            args.insert("failureThreshold".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        if let Some(ref v) = success_threshold {
            args.insert("successThreshold".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        if let Some(ref v) = endpoint_name {
            args.insert("endpointName".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        let result = self.client.invoke_capability("Aspire.Hosting/withHttpProbe", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResourceWithEndpoints::new(handle, self.client.clone()))
    }

    /// Excludes the resource from MCP server exposure
    pub fn exclude_from_mcp(&self) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        let result = self.client.invoke_capability("Aspire.Hosting/excludeFromMcp", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Sets the remote image name for publishing
    pub fn with_remote_image_name(&self, remote_image_name: &str) -> Result<IComputeResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("remoteImageName".to_string(), serde_json::to_value(&remote_image_name).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting/withRemoteImageName", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IComputeResource::new(handle, self.client.clone()))
    }

    /// Sets the remote image tag for publishing
    pub fn with_remote_image_tag(&self, remote_image_tag: &str) -> Result<IComputeResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("remoteImageTag".to_string(), serde_json::to_value(&remote_image_tag).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting/withRemoteImageTag", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IComputeResource::new(handle, self.client.clone()))
    }

    /// Adds a pipeline step to the resource
    pub fn with_pipeline_step_factory(&self, step_name: &str, callback: impl Fn(Vec<Value>) -> Value + Send + Sync + 'static, depends_on: Option<Vec<String>>, required_by: Option<Vec<String>>, tags: Option<Vec<String>>, description: Option<&str>) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("stepName".to_string(), serde_json::to_value(&step_name).unwrap_or(Value::Null));
        let callback_id = register_callback(callback);
        args.insert("callback".to_string(), Value::String(callback_id));
        if let Some(ref v) = depends_on {
            args.insert("dependsOn".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        if let Some(ref v) = required_by {
            args.insert("requiredBy".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        if let Some(ref v) = tags {
            args.insert("tags".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        if let Some(ref v) = description {
            args.insert("description".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        let result = self.client.invoke_capability("Aspire.Hosting/withPipelineStepFactory", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Configures pipeline step dependencies via a callback
    pub fn with_pipeline_configuration(&self, callback: impl Fn(Vec<Value>) -> Value + Send + Sync + 'static) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        let callback_id = register_callback(callback);
        args.insert("callback".to_string(), Value::String(callback_id));
        let result = self.client.invoke_capability("Aspire.Hosting/withPipelineConfiguration", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Gets the resource name
    pub fn get_resource_name(&self) -> Result<String, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("resource".to_string(), self.handle.to_json());
        let result = self.client.invoke_capability("Aspire.Hosting/getResourceName", args)?;
        Ok(serde_json::from_value(result)?)
    }

    /// Subscribes to the BeforeResourceStarted event
    pub fn on_before_resource_started(&self, callback: impl Fn(Vec<Value>) -> Value + Send + Sync + 'static) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        let callback_id = register_callback(callback);
        args.insert("callback".to_string(), Value::String(callback_id));
        let result = self.client.invoke_capability("Aspire.Hosting/onBeforeResourceStarted", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Subscribes to the ResourceStopped event
    pub fn on_resource_stopped(&self, callback: impl Fn(Vec<Value>) -> Value + Send + Sync + 'static) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        let callback_id = register_callback(callback);
        args.insert("callback".to_string(), Value::String(callback_id));
        let result = self.client.invoke_capability("Aspire.Hosting/onResourceStopped", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Subscribes to the InitializeResource event
    pub fn on_initialize_resource(&self, callback: impl Fn(Vec<Value>) -> Value + Send + Sync + 'static) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        let callback_id = register_callback(callback);
        args.insert("callback".to_string(), Value::String(callback_id));
        let result = self.client.invoke_capability("Aspire.Hosting/onInitializeResource", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Subscribes to the ResourceEndpointsAllocated event
    pub fn on_resource_endpoints_allocated(&self, callback: impl Fn(Vec<Value>) -> Value + Send + Sync + 'static) -> Result<IResourceWithEndpoints, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        let callback_id = register_callback(callback);
        args.insert("callback".to_string(), Value::String(callback_id));
        let result = self.client.invoke_capability("Aspire.Hosting/onResourceEndpointsAllocated", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResourceWithEndpoints::new(handle, self.client.clone()))
    }

    /// Subscribes to the ResourceReady event
    pub fn on_resource_ready(&self, callback: impl Fn(Vec<Value>) -> Value + Send + Sync + 'static) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        let callback_id = register_callback(callback);
        args.insert("callback".to_string(), Value::String(callback_id));
        let result = self.client.invoke_capability("Aspire.Hosting/onResourceReady", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
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

    /// Adds a dependency from a string or another resource
    pub fn with_union_dependency(&self, dependency: Value) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("dependency".to_string(), serde_json::to_value(&dependency).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting.CodeGeneration.Rust.Tests/withUnionDependency", args)?;
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

    /// Adds a label to the resource
    pub fn with_merge_label(&self, label: &str) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("label".to_string(), serde_json::to_value(&label).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting.CodeGeneration.Rust.Tests/withMergeLabel", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Adds a categorized label to the resource
    pub fn with_merge_label_categorized(&self, label: &str, category: &str) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("label".to_string(), serde_json::to_value(&label).unwrap_or(Value::Null));
        args.insert("category".to_string(), serde_json::to_value(&category).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting.CodeGeneration.Rust.Tests/withMergeLabelCategorized", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Configures a named endpoint
    pub fn with_merge_endpoint(&self, endpoint_name: &str, port: f64) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("endpointName".to_string(), serde_json::to_value(&endpoint_name).unwrap_or(Value::Null));
        args.insert("port".to_string(), serde_json::to_value(&port).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting.CodeGeneration.Rust.Tests/withMergeEndpoint", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Configures a named endpoint with scheme
    pub fn with_merge_endpoint_scheme(&self, endpoint_name: &str, port: f64, scheme: &str) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("endpointName".to_string(), serde_json::to_value(&endpoint_name).unwrap_or(Value::Null));
        args.insert("port".to_string(), serde_json::to_value(&port).unwrap_or(Value::Null));
        args.insert("scheme".to_string(), serde_json::to_value(&scheme).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting.CodeGeneration.Rust.Tests/withMergeEndpointScheme", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Configures resource logging
    pub fn with_merge_logging(&self, log_level: &str, enable_console: Option<bool>, max_files: Option<f64>) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("logLevel".to_string(), serde_json::to_value(&log_level).unwrap_or(Value::Null));
        if let Some(ref v) = enable_console {
            args.insert("enableConsole".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        if let Some(ref v) = max_files {
            args.insert("maxFiles".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        let result = self.client.invoke_capability("Aspire.Hosting.CodeGeneration.Rust.Tests/withMergeLogging", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Configures resource logging with file path
    pub fn with_merge_logging_path(&self, log_level: &str, log_path: &str, enable_console: Option<bool>, max_files: Option<f64>) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("logLevel".to_string(), serde_json::to_value(&log_level).unwrap_or(Value::Null));
        args.insert("logPath".to_string(), serde_json::to_value(&log_path).unwrap_or(Value::Null));
        if let Some(ref v) = enable_console {
            args.insert("enableConsole".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        if let Some(ref v) = max_files {
            args.insert("maxFiles".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        let result = self.client.invoke_capability("Aspire.Hosting.CodeGeneration.Rust.Tests/withMergeLoggingPath", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Configures a route
    pub fn with_merge_route(&self, path: &str, method: &str, handler: &str, priority: f64) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("path".to_string(), serde_json::to_value(&path).unwrap_or(Value::Null));
        args.insert("method".to_string(), serde_json::to_value(&method).unwrap_or(Value::Null));
        args.insert("handler".to_string(), serde_json::to_value(&handler).unwrap_or(Value::Null));
        args.insert("priority".to_string(), serde_json::to_value(&priority).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting.CodeGeneration.Rust.Tests/withMergeRoute", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Configures a route with middleware
    pub fn with_merge_route_middleware(&self, path: &str, method: &str, handler: &str, priority: f64, middleware: &str) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("path".to_string(), serde_json::to_value(&path).unwrap_or(Value::Null));
        args.insert("method".to_string(), serde_json::to_value(&method).unwrap_or(Value::Null));
        args.insert("handler".to_string(), serde_json::to_value(&handler).unwrap_or(Value::Null));
        args.insert("priority".to_string(), serde_json::to_value(&priority).unwrap_or(Value::Null));
        args.insert("middleware".to_string(), serde_json::to_value(&middleware).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting.CodeGeneration.Rust.Tests/withMergeRouteMiddleware", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }
}

/// Wrapper for Aspire.Hosting/Aspire.Hosting.ApplicationModel.CommandLineArgsCallbackContext
pub struct CommandLineArgsCallbackContext {
    handle: Handle,
    client: Arc<AspireClient>,
}

impl HasHandle for CommandLineArgsCallbackContext {
    fn handle(&self) -> &Handle {
        &self.handle
    }
}

impl CommandLineArgsCallbackContext {
    pub fn new(handle: Handle, client: Arc<AspireClient>) -> Self {
        Self { handle, client }
    }

    pub fn handle(&self) -> &Handle {
        &self.handle
    }

    pub fn client(&self) -> &Arc<AspireClient> {
        &self.client
    }

    /// Gets the Args property
    pub fn args(&self) -> AspireList<Value> {
        AspireList::with_getter(self.handle.clone(), self.client.clone(), "Aspire.Hosting.ApplicationModel/CommandLineArgsCallbackContext.args")
    }

    /// Gets the CancellationToken property
    pub fn cancellation_token(&self) -> Result<CancellationToken, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("context".to_string(), self.handle.to_json());
        let result = self.client.invoke_capability("Aspire.Hosting.ApplicationModel/CommandLineArgsCallbackContext.cancellationToken", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(CancellationToken::new(handle, self.client.clone()))
    }

    /// Gets the ExecutionContext property
    pub fn execution_context(&self) -> Result<DistributedApplicationExecutionContext, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("context".to_string(), self.handle.to_json());
        let result = self.client.invoke_capability("Aspire.Hosting.ApplicationModel/CommandLineArgsCallbackContext.executionContext", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(DistributedApplicationExecutionContext::new(handle, self.client.clone()))
    }

    /// Sets the ExecutionContext property
    pub fn set_execution_context(&self, value: &DistributedApplicationExecutionContext) -> Result<CommandLineArgsCallbackContext, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("context".to_string(), self.handle.to_json());
        args.insert("value".to_string(), value.handle().to_json());
        let result = self.client.invoke_capability("Aspire.Hosting.ApplicationModel/CommandLineArgsCallbackContext.setExecutionContext", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(CommandLineArgsCallbackContext::new(handle, self.client.clone()))
    }

    /// Gets the Logger property
    pub fn logger(&self) -> Result<ILogger, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("context".to_string(), self.handle.to_json());
        let result = self.client.invoke_capability("Aspire.Hosting.ApplicationModel/CommandLineArgsCallbackContext.logger", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(ILogger::new(handle, self.client.clone()))
    }

    /// Sets the Logger property
    pub fn set_logger(&self, value: &ILogger) -> Result<CommandLineArgsCallbackContext, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("context".to_string(), self.handle.to_json());
        args.insert("value".to_string(), value.handle().to_json());
        let result = self.client.invoke_capability("Aspire.Hosting.ApplicationModel/CommandLineArgsCallbackContext.setLogger", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(CommandLineArgsCallbackContext::new(handle, self.client.clone()))
    }

    /// Gets the Resource property
    pub fn resource(&self) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("context".to_string(), self.handle.to_json());
        let result = self.client.invoke_capability("Aspire.Hosting.ApplicationModel/CommandLineArgsCallbackContext.resource", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }
}

/// Wrapper for Aspire.Hosting/Aspire.Hosting.ApplicationModel.ConnectionStringAvailableEvent
pub struct ConnectionStringAvailableEvent {
    handle: Handle,
    client: Arc<AspireClient>,
}

impl HasHandle for ConnectionStringAvailableEvent {
    fn handle(&self) -> &Handle {
        &self.handle
    }
}

impl ConnectionStringAvailableEvent {
    pub fn new(handle: Handle, client: Arc<AspireClient>) -> Self {
        Self { handle, client }
    }

    pub fn handle(&self) -> &Handle {
        &self.handle
    }

    pub fn client(&self) -> &Arc<AspireClient> {
        &self.client
    }

    /// Gets the Resource property
    pub fn resource(&self) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("context".to_string(), self.handle.to_json());
        let result = self.client.invoke_capability("Aspire.Hosting.ApplicationModel/ConnectionStringAvailableEvent.resource", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Gets the Services property
    pub fn services(&self) -> Result<IServiceProvider, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("context".to_string(), self.handle.to_json());
        let result = self.client.invoke_capability("Aspire.Hosting.ApplicationModel/ConnectionStringAvailableEvent.services", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IServiceProvider::new(handle, self.client.clone()))
    }
}

/// Wrapper for Aspire.Hosting/Aspire.Hosting.ConnectionStringResource
pub struct ConnectionStringResource {
    handle: Handle,
    client: Arc<AspireClient>,
}

impl HasHandle for ConnectionStringResource {
    fn handle(&self) -> &Handle {
        &self.handle
    }
}

impl ConnectionStringResource {
    pub fn new(handle: Handle, client: Arc<AspireClient>) -> Self {
        Self { handle, client }
    }

    pub fn handle(&self) -> &Handle {
        &self.handle
    }

    pub fn client(&self) -> &Arc<AspireClient> {
        &self.client
    }

    /// Configures a resource to use a container registry
    pub fn with_container_registry(&self, registry: &IResource) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("registry".to_string(), registry.handle().to_json());
        let result = self.client.invoke_capability("Aspire.Hosting/withContainerRegistry", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Sets the base image for a Dockerfile build
    pub fn with_dockerfile_base_image(&self, build_image: Option<&str>, runtime_image: Option<&str>) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        if let Some(ref v) = build_image {
            args.insert("buildImage".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        if let Some(ref v) = runtime_image {
            args.insert("runtimeImage".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        let result = self.client.invoke_capability("Aspire.Hosting/withDockerfileBaseImage", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Adds a required command dependency
    pub fn with_required_command(&self, command: &str, help_link: Option<&str>) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("command".to_string(), serde_json::to_value(&command).unwrap_or(Value::Null));
        if let Some(ref v) = help_link {
            args.insert("helpLink".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        let result = self.client.invoke_capability("Aspire.Hosting/withRequiredCommand", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Adds a connection property with a reference expression
    pub fn with_connection_property(&self, name: &str, value: ReferenceExpression) -> Result<IResourceWithConnectionString, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("name".to_string(), serde_json::to_value(&name).unwrap_or(Value::Null));
        args.insert("value".to_string(), serde_json::to_value(&value).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting/withConnectionProperty", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResourceWithConnectionString::new(handle, self.client.clone()))
    }

    /// Adds a connection property with a string value
    pub fn with_connection_property_value(&self, name: &str, value: &str) -> Result<IResourceWithConnectionString, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("name".to_string(), serde_json::to_value(&name).unwrap_or(Value::Null));
        args.insert("value".to_string(), serde_json::to_value(&value).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting/withConnectionPropertyValue", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResourceWithConnectionString::new(handle, self.client.clone()))
    }

    /// Gets a connection property by key
    pub fn get_connection_property(&self, key: &str) -> Result<ReferenceExpression, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("resource".to_string(), self.handle.to_json());
        args.insert("key".to_string(), serde_json::to_value(&key).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting/getConnectionProperty", args)?;
        Ok(serde_json::from_value(result)?)
    }

    /// Customizes displayed URLs via callback
    pub fn with_urls_callback(&self, callback: impl Fn(Vec<Value>) -> Value + Send + Sync + 'static) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        let callback_id = register_callback(callback);
        args.insert("callback".to_string(), Value::String(callback_id));
        let result = self.client.invoke_capability("Aspire.Hosting/withUrlsCallback", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Adds or modifies displayed URLs
    pub fn with_url(&self, url: &str, display_text: Option<&str>) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("url".to_string(), serde_json::to_value(&url).unwrap_or(Value::Null));
        if let Some(ref v) = display_text {
            args.insert("displayText".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        let result = self.client.invoke_capability("Aspire.Hosting/withUrl", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Adds a URL using a reference expression
    pub fn with_url_expression(&self, url: ReferenceExpression, display_text: Option<&str>) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("url".to_string(), serde_json::to_value(&url).unwrap_or(Value::Null));
        if let Some(ref v) = display_text {
            args.insert("displayText".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        let result = self.client.invoke_capability("Aspire.Hosting/withUrlExpression", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Customizes the URL for a specific endpoint via callback
    pub fn with_url_for_endpoint(&self, endpoint_name: &str, callback: impl Fn(Vec<Value>) -> Value + Send + Sync + 'static) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("endpointName".to_string(), serde_json::to_value(&endpoint_name).unwrap_or(Value::Null));
        let callback_id = register_callback(callback);
        args.insert("callback".to_string(), Value::String(callback_id));
        let result = self.client.invoke_capability("Aspire.Hosting/withUrlForEndpoint", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Excludes the resource from the deployment manifest
    pub fn exclude_from_manifest(&self) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        let result = self.client.invoke_capability("Aspire.Hosting/excludeFromManifest", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Waits for another resource to be ready
    pub fn wait_for(&self, dependency: &IResource) -> Result<IResourceWithWaitSupport, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("dependency".to_string(), dependency.handle().to_json());
        let result = self.client.invoke_capability("Aspire.Hosting/waitForResource", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResourceWithWaitSupport::new(handle, self.client.clone()))
    }

    /// Waits for another resource with specific behavior
    pub fn wait_for_with_behavior(&self, dependency: &IResource, wait_behavior: WaitBehavior) -> Result<IResourceWithWaitSupport, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("dependency".to_string(), dependency.handle().to_json());
        args.insert("waitBehavior".to_string(), serde_json::to_value(&wait_behavior).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting/waitForWithBehavior", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResourceWithWaitSupport::new(handle, self.client.clone()))
    }

    /// Waits for another resource to start
    pub fn wait_for_start(&self, dependency: &IResource) -> Result<IResourceWithWaitSupport, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("dependency".to_string(), dependency.handle().to_json());
        let result = self.client.invoke_capability("Aspire.Hosting/waitForResourceStart", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResourceWithWaitSupport::new(handle, self.client.clone()))
    }

    /// Waits for another resource to start with specific behavior
    pub fn wait_for_start_with_behavior(&self, dependency: &IResource, wait_behavior: WaitBehavior) -> Result<IResourceWithWaitSupport, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("dependency".to_string(), dependency.handle().to_json());
        args.insert("waitBehavior".to_string(), serde_json::to_value(&wait_behavior).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting/waitForStartWithBehavior", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResourceWithWaitSupport::new(handle, self.client.clone()))
    }

    /// Prevents resource from starting automatically
    pub fn with_explicit_start(&self) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        let result = self.client.invoke_capability("Aspire.Hosting/withExplicitStart", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Waits for resource completion
    pub fn wait_for_completion(&self, dependency: &IResource, exit_code: Option<f64>) -> Result<IResourceWithWaitSupport, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("dependency".to_string(), dependency.handle().to_json());
        if let Some(ref v) = exit_code {
            args.insert("exitCode".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        let result = self.client.invoke_capability("Aspire.Hosting/waitForResourceCompletion", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResourceWithWaitSupport::new(handle, self.client.clone()))
    }

    /// Adds a health check by key
    pub fn with_health_check(&self, key: &str) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("key".to_string(), serde_json::to_value(&key).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting/withHealthCheck", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Adds a resource command
    pub fn with_command(&self, name: &str, display_name: &str, execute_command: impl Fn(Vec<Value>) -> Value + Send + Sync + 'static, command_options: Option<CommandOptions>) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("name".to_string(), serde_json::to_value(&name).unwrap_or(Value::Null));
        args.insert("displayName".to_string(), serde_json::to_value(&display_name).unwrap_or(Value::Null));
        let callback_id = register_callback(execute_command);
        args.insert("executeCommand".to_string(), Value::String(callback_id));
        if let Some(ref v) = command_options {
            args.insert("commandOptions".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        let result = self.client.invoke_capability("Aspire.Hosting/withCommand", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Sets the parent relationship
    pub fn with_parent_relationship(&self, parent: &IResource) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("parent".to_string(), parent.handle().to_json());
        let result = self.client.invoke_capability("Aspire.Hosting/withBuilderParentRelationship", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Sets a child relationship
    pub fn with_child_relationship(&self, child: &IResource) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("child".to_string(), child.handle().to_json());
        let result = self.client.invoke_capability("Aspire.Hosting/withBuilderChildRelationship", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Sets the icon for the resource
    pub fn with_icon_name(&self, icon_name: &str, icon_variant: Option<IconVariant>) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("iconName".to_string(), serde_json::to_value(&icon_name).unwrap_or(Value::Null));
        if let Some(ref v) = icon_variant {
            args.insert("iconVariant".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        let result = self.client.invoke_capability("Aspire.Hosting/withIconName", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Excludes the resource from MCP server exposure
    pub fn exclude_from_mcp(&self) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        let result = self.client.invoke_capability("Aspire.Hosting/excludeFromMcp", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Adds a pipeline step to the resource
    pub fn with_pipeline_step_factory(&self, step_name: &str, callback: impl Fn(Vec<Value>) -> Value + Send + Sync + 'static, depends_on: Option<Vec<String>>, required_by: Option<Vec<String>>, tags: Option<Vec<String>>, description: Option<&str>) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("stepName".to_string(), serde_json::to_value(&step_name).unwrap_or(Value::Null));
        let callback_id = register_callback(callback);
        args.insert("callback".to_string(), Value::String(callback_id));
        if let Some(ref v) = depends_on {
            args.insert("dependsOn".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        if let Some(ref v) = required_by {
            args.insert("requiredBy".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        if let Some(ref v) = tags {
            args.insert("tags".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        if let Some(ref v) = description {
            args.insert("description".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        let result = self.client.invoke_capability("Aspire.Hosting/withPipelineStepFactory", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Configures pipeline step dependencies via a callback
    pub fn with_pipeline_configuration(&self, callback: impl Fn(Vec<Value>) -> Value + Send + Sync + 'static) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        let callback_id = register_callback(callback);
        args.insert("callback".to_string(), Value::String(callback_id));
        let result = self.client.invoke_capability("Aspire.Hosting/withPipelineConfiguration", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Gets the resource name
    pub fn get_resource_name(&self) -> Result<String, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("resource".to_string(), self.handle.to_json());
        let result = self.client.invoke_capability("Aspire.Hosting/getResourceName", args)?;
        Ok(serde_json::from_value(result)?)
    }

    /// Subscribes to the BeforeResourceStarted event
    pub fn on_before_resource_started(&self, callback: impl Fn(Vec<Value>) -> Value + Send + Sync + 'static) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        let callback_id = register_callback(callback);
        args.insert("callback".to_string(), Value::String(callback_id));
        let result = self.client.invoke_capability("Aspire.Hosting/onBeforeResourceStarted", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Subscribes to the ResourceStopped event
    pub fn on_resource_stopped(&self, callback: impl Fn(Vec<Value>) -> Value + Send + Sync + 'static) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        let callback_id = register_callback(callback);
        args.insert("callback".to_string(), Value::String(callback_id));
        let result = self.client.invoke_capability("Aspire.Hosting/onResourceStopped", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Subscribes to the ConnectionStringAvailable event
    pub fn on_connection_string_available(&self, callback: impl Fn(Vec<Value>) -> Value + Send + Sync + 'static) -> Result<IResourceWithConnectionString, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        let callback_id = register_callback(callback);
        args.insert("callback".to_string(), Value::String(callback_id));
        let result = self.client.invoke_capability("Aspire.Hosting/onConnectionStringAvailable", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResourceWithConnectionString::new(handle, self.client.clone()))
    }

    /// Subscribes to the InitializeResource event
    pub fn on_initialize_resource(&self, callback: impl Fn(Vec<Value>) -> Value + Send + Sync + 'static) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        let callback_id = register_callback(callback);
        args.insert("callback".to_string(), Value::String(callback_id));
        let result = self.client.invoke_capability("Aspire.Hosting/onInitializeResource", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Subscribes to the ResourceReady event
    pub fn on_resource_ready(&self, callback: impl Fn(Vec<Value>) -> Value + Send + Sync + 'static) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        let callback_id = register_callback(callback);
        args.insert("callback".to_string(), Value::String(callback_id));
        let result = self.client.invoke_capability("Aspire.Hosting/onResourceReady", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
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

    /// Sets the connection string using a reference expression
    pub fn with_connection_string(&self, connection_string: ReferenceExpression) -> Result<IResourceWithConnectionString, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("connectionString".to_string(), serde_json::to_value(&connection_string).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting.CodeGeneration.Rust.Tests/withConnectionString", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResourceWithConnectionString::new(handle, self.client.clone()))
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

    /// Sets connection string using direct interface target
    pub fn with_connection_string_direct(&self, connection_string: &str) -> Result<IResourceWithConnectionString, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("connectionString".to_string(), serde_json::to_value(&connection_string).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting.CodeGeneration.Rust.Tests/withConnectionStringDirect", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResourceWithConnectionString::new(handle, self.client.clone()))
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

    /// Adds a dependency from a string or another resource
    pub fn with_union_dependency(&self, dependency: Value) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("dependency".to_string(), serde_json::to_value(&dependency).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting.CodeGeneration.Rust.Tests/withUnionDependency", args)?;
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

    /// Adds a label to the resource
    pub fn with_merge_label(&self, label: &str) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("label".to_string(), serde_json::to_value(&label).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting.CodeGeneration.Rust.Tests/withMergeLabel", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Adds a categorized label to the resource
    pub fn with_merge_label_categorized(&self, label: &str, category: &str) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("label".to_string(), serde_json::to_value(&label).unwrap_or(Value::Null));
        args.insert("category".to_string(), serde_json::to_value(&category).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting.CodeGeneration.Rust.Tests/withMergeLabelCategorized", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Configures a named endpoint
    pub fn with_merge_endpoint(&self, endpoint_name: &str, port: f64) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("endpointName".to_string(), serde_json::to_value(&endpoint_name).unwrap_or(Value::Null));
        args.insert("port".to_string(), serde_json::to_value(&port).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting.CodeGeneration.Rust.Tests/withMergeEndpoint", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Configures a named endpoint with scheme
    pub fn with_merge_endpoint_scheme(&self, endpoint_name: &str, port: f64, scheme: &str) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("endpointName".to_string(), serde_json::to_value(&endpoint_name).unwrap_or(Value::Null));
        args.insert("port".to_string(), serde_json::to_value(&port).unwrap_or(Value::Null));
        args.insert("scheme".to_string(), serde_json::to_value(&scheme).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting.CodeGeneration.Rust.Tests/withMergeEndpointScheme", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Configures resource logging
    pub fn with_merge_logging(&self, log_level: &str, enable_console: Option<bool>, max_files: Option<f64>) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("logLevel".to_string(), serde_json::to_value(&log_level).unwrap_or(Value::Null));
        if let Some(ref v) = enable_console {
            args.insert("enableConsole".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        if let Some(ref v) = max_files {
            args.insert("maxFiles".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        let result = self.client.invoke_capability("Aspire.Hosting.CodeGeneration.Rust.Tests/withMergeLogging", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Configures resource logging with file path
    pub fn with_merge_logging_path(&self, log_level: &str, log_path: &str, enable_console: Option<bool>, max_files: Option<f64>) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("logLevel".to_string(), serde_json::to_value(&log_level).unwrap_or(Value::Null));
        args.insert("logPath".to_string(), serde_json::to_value(&log_path).unwrap_or(Value::Null));
        if let Some(ref v) = enable_console {
            args.insert("enableConsole".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        if let Some(ref v) = max_files {
            args.insert("maxFiles".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        let result = self.client.invoke_capability("Aspire.Hosting.CodeGeneration.Rust.Tests/withMergeLoggingPath", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Configures a route
    pub fn with_merge_route(&self, path: &str, method: &str, handler: &str, priority: f64) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("path".to_string(), serde_json::to_value(&path).unwrap_or(Value::Null));
        args.insert("method".to_string(), serde_json::to_value(&method).unwrap_or(Value::Null));
        args.insert("handler".to_string(), serde_json::to_value(&handler).unwrap_or(Value::Null));
        args.insert("priority".to_string(), serde_json::to_value(&priority).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting.CodeGeneration.Rust.Tests/withMergeRoute", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Configures a route with middleware
    pub fn with_merge_route_middleware(&self, path: &str, method: &str, handler: &str, priority: f64, middleware: &str) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("path".to_string(), serde_json::to_value(&path).unwrap_or(Value::Null));
        args.insert("method".to_string(), serde_json::to_value(&method).unwrap_or(Value::Null));
        args.insert("handler".to_string(), serde_json::to_value(&handler).unwrap_or(Value::Null));
        args.insert("priority".to_string(), serde_json::to_value(&priority).unwrap_or(Value::Null));
        args.insert("middleware".to_string(), serde_json::to_value(&middleware).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting.CodeGeneration.Rust.Tests/withMergeRouteMiddleware", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }
}

/// Wrapper for Aspire.Hosting/Aspire.Hosting.ApplicationModel.ContainerRegistryResource
pub struct ContainerRegistryResource {
    handle: Handle,
    client: Arc<AspireClient>,
}

impl HasHandle for ContainerRegistryResource {
    fn handle(&self) -> &Handle {
        &self.handle
    }
}

impl ContainerRegistryResource {
    pub fn new(handle: Handle, client: Arc<AspireClient>) -> Self {
        Self { handle, client }
    }

    pub fn handle(&self) -> &Handle {
        &self.handle
    }

    pub fn client(&self) -> &Arc<AspireClient> {
        &self.client
    }

    /// Configures a resource to use a container registry
    pub fn with_container_registry(&self, registry: &IResource) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("registry".to_string(), registry.handle().to_json());
        let result = self.client.invoke_capability("Aspire.Hosting/withContainerRegistry", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Sets the base image for a Dockerfile build
    pub fn with_dockerfile_base_image(&self, build_image: Option<&str>, runtime_image: Option<&str>) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        if let Some(ref v) = build_image {
            args.insert("buildImage".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        if let Some(ref v) = runtime_image {
            args.insert("runtimeImage".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        let result = self.client.invoke_capability("Aspire.Hosting/withDockerfileBaseImage", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Adds a required command dependency
    pub fn with_required_command(&self, command: &str, help_link: Option<&str>) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("command".to_string(), serde_json::to_value(&command).unwrap_or(Value::Null));
        if let Some(ref v) = help_link {
            args.insert("helpLink".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        let result = self.client.invoke_capability("Aspire.Hosting/withRequiredCommand", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Customizes displayed URLs via callback
    pub fn with_urls_callback(&self, callback: impl Fn(Vec<Value>) -> Value + Send + Sync + 'static) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        let callback_id = register_callback(callback);
        args.insert("callback".to_string(), Value::String(callback_id));
        let result = self.client.invoke_capability("Aspire.Hosting/withUrlsCallback", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Adds or modifies displayed URLs
    pub fn with_url(&self, url: &str, display_text: Option<&str>) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("url".to_string(), serde_json::to_value(&url).unwrap_or(Value::Null));
        if let Some(ref v) = display_text {
            args.insert("displayText".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        let result = self.client.invoke_capability("Aspire.Hosting/withUrl", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Adds a URL using a reference expression
    pub fn with_url_expression(&self, url: ReferenceExpression, display_text: Option<&str>) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("url".to_string(), serde_json::to_value(&url).unwrap_or(Value::Null));
        if let Some(ref v) = display_text {
            args.insert("displayText".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        let result = self.client.invoke_capability("Aspire.Hosting/withUrlExpression", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Customizes the URL for a specific endpoint via callback
    pub fn with_url_for_endpoint(&self, endpoint_name: &str, callback: impl Fn(Vec<Value>) -> Value + Send + Sync + 'static) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("endpointName".to_string(), serde_json::to_value(&endpoint_name).unwrap_or(Value::Null));
        let callback_id = register_callback(callback);
        args.insert("callback".to_string(), Value::String(callback_id));
        let result = self.client.invoke_capability("Aspire.Hosting/withUrlForEndpoint", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Excludes the resource from the deployment manifest
    pub fn exclude_from_manifest(&self) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        let result = self.client.invoke_capability("Aspire.Hosting/excludeFromManifest", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Prevents resource from starting automatically
    pub fn with_explicit_start(&self) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        let result = self.client.invoke_capability("Aspire.Hosting/withExplicitStart", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Adds a health check by key
    pub fn with_health_check(&self, key: &str) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("key".to_string(), serde_json::to_value(&key).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting/withHealthCheck", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Adds a resource command
    pub fn with_command(&self, name: &str, display_name: &str, execute_command: impl Fn(Vec<Value>) -> Value + Send + Sync + 'static, command_options: Option<CommandOptions>) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("name".to_string(), serde_json::to_value(&name).unwrap_or(Value::Null));
        args.insert("displayName".to_string(), serde_json::to_value(&display_name).unwrap_or(Value::Null));
        let callback_id = register_callback(execute_command);
        args.insert("executeCommand".to_string(), Value::String(callback_id));
        if let Some(ref v) = command_options {
            args.insert("commandOptions".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        let result = self.client.invoke_capability("Aspire.Hosting/withCommand", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Sets the parent relationship
    pub fn with_parent_relationship(&self, parent: &IResource) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("parent".to_string(), parent.handle().to_json());
        let result = self.client.invoke_capability("Aspire.Hosting/withBuilderParentRelationship", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Sets a child relationship
    pub fn with_child_relationship(&self, child: &IResource) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("child".to_string(), child.handle().to_json());
        let result = self.client.invoke_capability("Aspire.Hosting/withBuilderChildRelationship", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Sets the icon for the resource
    pub fn with_icon_name(&self, icon_name: &str, icon_variant: Option<IconVariant>) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("iconName".to_string(), serde_json::to_value(&icon_name).unwrap_or(Value::Null));
        if let Some(ref v) = icon_variant {
            args.insert("iconVariant".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        let result = self.client.invoke_capability("Aspire.Hosting/withIconName", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Excludes the resource from MCP server exposure
    pub fn exclude_from_mcp(&self) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        let result = self.client.invoke_capability("Aspire.Hosting/excludeFromMcp", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Adds a pipeline step to the resource
    pub fn with_pipeline_step_factory(&self, step_name: &str, callback: impl Fn(Vec<Value>) -> Value + Send + Sync + 'static, depends_on: Option<Vec<String>>, required_by: Option<Vec<String>>, tags: Option<Vec<String>>, description: Option<&str>) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("stepName".to_string(), serde_json::to_value(&step_name).unwrap_or(Value::Null));
        let callback_id = register_callback(callback);
        args.insert("callback".to_string(), Value::String(callback_id));
        if let Some(ref v) = depends_on {
            args.insert("dependsOn".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        if let Some(ref v) = required_by {
            args.insert("requiredBy".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        if let Some(ref v) = tags {
            args.insert("tags".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        if let Some(ref v) = description {
            args.insert("description".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        let result = self.client.invoke_capability("Aspire.Hosting/withPipelineStepFactory", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Configures pipeline step dependencies via a callback
    pub fn with_pipeline_configuration(&self, callback: impl Fn(Vec<Value>) -> Value + Send + Sync + 'static) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        let callback_id = register_callback(callback);
        args.insert("callback".to_string(), Value::String(callback_id));
        let result = self.client.invoke_capability("Aspire.Hosting/withPipelineConfiguration", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Gets the resource name
    pub fn get_resource_name(&self) -> Result<String, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("resource".to_string(), self.handle.to_json());
        let result = self.client.invoke_capability("Aspire.Hosting/getResourceName", args)?;
        Ok(serde_json::from_value(result)?)
    }

    /// Subscribes to the BeforeResourceStarted event
    pub fn on_before_resource_started(&self, callback: impl Fn(Vec<Value>) -> Value + Send + Sync + 'static) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        let callback_id = register_callback(callback);
        args.insert("callback".to_string(), Value::String(callback_id));
        let result = self.client.invoke_capability("Aspire.Hosting/onBeforeResourceStarted", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Subscribes to the ResourceStopped event
    pub fn on_resource_stopped(&self, callback: impl Fn(Vec<Value>) -> Value + Send + Sync + 'static) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        let callback_id = register_callback(callback);
        args.insert("callback".to_string(), Value::String(callback_id));
        let result = self.client.invoke_capability("Aspire.Hosting/onResourceStopped", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Subscribes to the InitializeResource event
    pub fn on_initialize_resource(&self, callback: impl Fn(Vec<Value>) -> Value + Send + Sync + 'static) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        let callback_id = register_callback(callback);
        args.insert("callback".to_string(), Value::String(callback_id));
        let result = self.client.invoke_capability("Aspire.Hosting/onInitializeResource", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Subscribes to the ResourceReady event
    pub fn on_resource_ready(&self, callback: impl Fn(Vec<Value>) -> Value + Send + Sync + 'static) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        let callback_id = register_callback(callback);
        args.insert("callback".to_string(), Value::String(callback_id));
        let result = self.client.invoke_capability("Aspire.Hosting/onResourceReady", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
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

    /// Adds a dependency from a string or another resource
    pub fn with_union_dependency(&self, dependency: Value) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("dependency".to_string(), serde_json::to_value(&dependency).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting.CodeGeneration.Rust.Tests/withUnionDependency", args)?;
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

    /// Adds a label to the resource
    pub fn with_merge_label(&self, label: &str) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("label".to_string(), serde_json::to_value(&label).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting.CodeGeneration.Rust.Tests/withMergeLabel", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Adds a categorized label to the resource
    pub fn with_merge_label_categorized(&self, label: &str, category: &str) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("label".to_string(), serde_json::to_value(&label).unwrap_or(Value::Null));
        args.insert("category".to_string(), serde_json::to_value(&category).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting.CodeGeneration.Rust.Tests/withMergeLabelCategorized", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Configures a named endpoint
    pub fn with_merge_endpoint(&self, endpoint_name: &str, port: f64) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("endpointName".to_string(), serde_json::to_value(&endpoint_name).unwrap_or(Value::Null));
        args.insert("port".to_string(), serde_json::to_value(&port).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting.CodeGeneration.Rust.Tests/withMergeEndpoint", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Configures a named endpoint with scheme
    pub fn with_merge_endpoint_scheme(&self, endpoint_name: &str, port: f64, scheme: &str) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("endpointName".to_string(), serde_json::to_value(&endpoint_name).unwrap_or(Value::Null));
        args.insert("port".to_string(), serde_json::to_value(&port).unwrap_or(Value::Null));
        args.insert("scheme".to_string(), serde_json::to_value(&scheme).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting.CodeGeneration.Rust.Tests/withMergeEndpointScheme", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Configures resource logging
    pub fn with_merge_logging(&self, log_level: &str, enable_console: Option<bool>, max_files: Option<f64>) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("logLevel".to_string(), serde_json::to_value(&log_level).unwrap_or(Value::Null));
        if let Some(ref v) = enable_console {
            args.insert("enableConsole".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        if let Some(ref v) = max_files {
            args.insert("maxFiles".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        let result = self.client.invoke_capability("Aspire.Hosting.CodeGeneration.Rust.Tests/withMergeLogging", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Configures resource logging with file path
    pub fn with_merge_logging_path(&self, log_level: &str, log_path: &str, enable_console: Option<bool>, max_files: Option<f64>) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("logLevel".to_string(), serde_json::to_value(&log_level).unwrap_or(Value::Null));
        args.insert("logPath".to_string(), serde_json::to_value(&log_path).unwrap_or(Value::Null));
        if let Some(ref v) = enable_console {
            args.insert("enableConsole".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        if let Some(ref v) = max_files {
            args.insert("maxFiles".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        let result = self.client.invoke_capability("Aspire.Hosting.CodeGeneration.Rust.Tests/withMergeLoggingPath", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Configures a route
    pub fn with_merge_route(&self, path: &str, method: &str, handler: &str, priority: f64) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("path".to_string(), serde_json::to_value(&path).unwrap_or(Value::Null));
        args.insert("method".to_string(), serde_json::to_value(&method).unwrap_or(Value::Null));
        args.insert("handler".to_string(), serde_json::to_value(&handler).unwrap_or(Value::Null));
        args.insert("priority".to_string(), serde_json::to_value(&priority).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting.CodeGeneration.Rust.Tests/withMergeRoute", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Configures a route with middleware
    pub fn with_merge_route_middleware(&self, path: &str, method: &str, handler: &str, priority: f64, middleware: &str) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("path".to_string(), serde_json::to_value(&path).unwrap_or(Value::Null));
        args.insert("method".to_string(), serde_json::to_value(&method).unwrap_or(Value::Null));
        args.insert("handler".to_string(), serde_json::to_value(&handler).unwrap_or(Value::Null));
        args.insert("priority".to_string(), serde_json::to_value(&priority).unwrap_or(Value::Null));
        args.insert("middleware".to_string(), serde_json::to_value(&middleware).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting.CodeGeneration.Rust.Tests/withMergeRouteMiddleware", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }
}

/// Wrapper for Aspire.Hosting/Aspire.Hosting.ApplicationModel.ContainerResource
pub struct ContainerResource {
    handle: Handle,
    client: Arc<AspireClient>,
}

impl HasHandle for ContainerResource {
    fn handle(&self) -> &Handle {
        &self.handle
    }
}

impl ContainerResource {
    pub fn new(handle: Handle, client: Arc<AspireClient>) -> Self {
        Self { handle, client }
    }

    pub fn handle(&self) -> &Handle {
        &self.handle
    }

    pub fn client(&self) -> &Arc<AspireClient> {
        &self.client
    }

    /// Configures a resource to use a container registry
    pub fn with_container_registry(&self, registry: &IResource) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("registry".to_string(), registry.handle().to_json());
        let result = self.client.invoke_capability("Aspire.Hosting/withContainerRegistry", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Adds a bind mount
    pub fn with_bind_mount(&self, source: &str, target: &str, is_read_only: Option<bool>) -> Result<ContainerResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("source".to_string(), serde_json::to_value(&source).unwrap_or(Value::Null));
        args.insert("target".to_string(), serde_json::to_value(&target).unwrap_or(Value::Null));
        if let Some(ref v) = is_read_only {
            args.insert("isReadOnly".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        let result = self.client.invoke_capability("Aspire.Hosting/withBindMount", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(ContainerResource::new(handle, self.client.clone()))
    }

    /// Sets the container entrypoint
    pub fn with_entrypoint(&self, entrypoint: &str) -> Result<ContainerResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("entrypoint".to_string(), serde_json::to_value(&entrypoint).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting/withEntrypoint", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(ContainerResource::new(handle, self.client.clone()))
    }

    /// Sets the container image tag
    pub fn with_image_tag(&self, tag: &str) -> Result<ContainerResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("tag".to_string(), serde_json::to_value(&tag).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting/withImageTag", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(ContainerResource::new(handle, self.client.clone()))
    }

    /// Sets the container image registry
    pub fn with_image_registry(&self, registry: &str) -> Result<ContainerResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("registry".to_string(), serde_json::to_value(&registry).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting/withImageRegistry", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(ContainerResource::new(handle, self.client.clone()))
    }

    /// Sets the container image
    pub fn with_image(&self, image: &str, tag: Option<&str>) -> Result<ContainerResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("image".to_string(), serde_json::to_value(&image).unwrap_or(Value::Null));
        if let Some(ref v) = tag {
            args.insert("tag".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        let result = self.client.invoke_capability("Aspire.Hosting/withImage", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(ContainerResource::new(handle, self.client.clone()))
    }

    /// Sets the image SHA256 digest
    pub fn with_image_sha256(&self, sha256: &str) -> Result<ContainerResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("sha256".to_string(), serde_json::to_value(&sha256).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting/withImageSHA256", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(ContainerResource::new(handle, self.client.clone()))
    }

    /// Adds runtime arguments for the container
    pub fn with_container_runtime_args(&self, args: Vec<String>) -> Result<ContainerResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("args".to_string(), serde_json::to_value(&args).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting/withContainerRuntimeArgs", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(ContainerResource::new(handle, self.client.clone()))
    }

    /// Sets the lifetime behavior of the container resource
    pub fn with_lifetime(&self, lifetime: ContainerLifetime) -> Result<ContainerResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("lifetime".to_string(), serde_json::to_value(&lifetime).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting/withLifetime", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(ContainerResource::new(handle, self.client.clone()))
    }

    /// Sets the container image pull policy
    pub fn with_image_pull_policy(&self, pull_policy: ImagePullPolicy) -> Result<ContainerResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("pullPolicy".to_string(), serde_json::to_value(&pull_policy).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting/withImagePullPolicy", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(ContainerResource::new(handle, self.client.clone()))
    }

    /// Configures the resource to be published as a container
    pub fn publish_as_container(&self) -> Result<ContainerResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        let result = self.client.invoke_capability("Aspire.Hosting/publishAsContainer", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(ContainerResource::new(handle, self.client.clone()))
    }

    /// Configures the resource to use a Dockerfile
    pub fn with_dockerfile(&self, context_path: &str, dockerfile_path: Option<&str>, stage: Option<&str>) -> Result<ContainerResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("contextPath".to_string(), serde_json::to_value(&context_path).unwrap_or(Value::Null));
        if let Some(ref v) = dockerfile_path {
            args.insert("dockerfilePath".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        if let Some(ref v) = stage {
            args.insert("stage".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        let result = self.client.invoke_capability("Aspire.Hosting/withDockerfile", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(ContainerResource::new(handle, self.client.clone()))
    }

    /// Sets the container name
    pub fn with_container_name(&self, name: &str) -> Result<ContainerResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("name".to_string(), serde_json::to_value(&name).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting/withContainerName", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(ContainerResource::new(handle, self.client.clone()))
    }

    /// Adds a build argument from a parameter resource
    pub fn with_build_arg(&self, name: &str, value: &ParameterResource) -> Result<ContainerResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("name".to_string(), serde_json::to_value(&name).unwrap_or(Value::Null));
        args.insert("value".to_string(), value.handle().to_json());
        let result = self.client.invoke_capability("Aspire.Hosting/withParameterBuildArg", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(ContainerResource::new(handle, self.client.clone()))
    }

    /// Adds a build secret from a parameter resource
    pub fn with_build_secret(&self, name: &str, value: &ParameterResource) -> Result<ContainerResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("name".to_string(), serde_json::to_value(&name).unwrap_or(Value::Null));
        args.insert("value".to_string(), value.handle().to_json());
        let result = self.client.invoke_capability("Aspire.Hosting/withParameterBuildSecret", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(ContainerResource::new(handle, self.client.clone()))
    }

    /// Configures endpoint proxy support
    pub fn with_endpoint_proxy_support(&self, proxy_enabled: bool) -> Result<ContainerResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("proxyEnabled".to_string(), serde_json::to_value(&proxy_enabled).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting/withEndpointProxySupport", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(ContainerResource::new(handle, self.client.clone()))
    }

    /// Sets the base image for a Dockerfile build
    pub fn with_dockerfile_base_image(&self, build_image: Option<&str>, runtime_image: Option<&str>) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        if let Some(ref v) = build_image {
            args.insert("buildImage".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        if let Some(ref v) = runtime_image {
            args.insert("runtimeImage".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        let result = self.client.invoke_capability("Aspire.Hosting/withDockerfileBaseImage", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Adds a network alias for the container
    pub fn with_container_network_alias(&self, alias: &str) -> Result<ContainerResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("alias".to_string(), serde_json::to_value(&alias).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting/withContainerNetworkAlias", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(ContainerResource::new(handle, self.client.clone()))
    }

    /// Configures an MCP server endpoint on the resource
    pub fn with_mcp_server(&self, path: Option<&str>, endpoint_name: Option<&str>) -> Result<IResourceWithEndpoints, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        if let Some(ref v) = path {
            args.insert("path".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        if let Some(ref v) = endpoint_name {
            args.insert("endpointName".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        let result = self.client.invoke_capability("Aspire.Hosting/withMcpServer", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResourceWithEndpoints::new(handle, self.client.clone()))
    }

    /// Configures OTLP telemetry export
    pub fn with_otlp_exporter(&self) -> Result<IResourceWithEnvironment, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        let result = self.client.invoke_capability("Aspire.Hosting/withOtlpExporter", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResourceWithEnvironment::new(handle, self.client.clone()))
    }

    /// Configures OTLP telemetry export with specific protocol
    pub fn with_otlp_exporter_protocol(&self, protocol: OtlpProtocol) -> Result<IResourceWithEnvironment, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("protocol".to_string(), serde_json::to_value(&protocol).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting/withOtlpExporterProtocol", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResourceWithEnvironment::new(handle, self.client.clone()))
    }

    /// Publishes the resource as a connection string
    pub fn publish_as_connection_string(&self) -> Result<ContainerResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        let result = self.client.invoke_capability("Aspire.Hosting/publishAsConnectionString", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(ContainerResource::new(handle, self.client.clone()))
    }

    /// Adds a required command dependency
    pub fn with_required_command(&self, command: &str, help_link: Option<&str>) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("command".to_string(), serde_json::to_value(&command).unwrap_or(Value::Null));
        if let Some(ref v) = help_link {
            args.insert("helpLink".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        let result = self.client.invoke_capability("Aspire.Hosting/withRequiredCommand", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Sets an environment variable
    pub fn with_environment(&self, name: &str, value: Value) -> Result<IResourceWithEnvironment, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("name".to_string(), serde_json::to_value(&name).unwrap_or(Value::Null));
        args.insert("value".to_string(), serde_json::to_value(&value).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting/withEnvironment", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResourceWithEnvironment::new(handle, self.client.clone()))
    }

    /// Sets an environment variable from a reference expression
    pub fn with_environment_expression(&self, name: &str, value: ReferenceExpression) -> Result<IResourceWithEnvironment, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("name".to_string(), serde_json::to_value(&name).unwrap_or(Value::Null));
        args.insert("value".to_string(), serde_json::to_value(&value).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting/withEnvironmentExpression", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResourceWithEnvironment::new(handle, self.client.clone()))
    }

    /// Sets environment variables via callback
    pub fn with_environment_callback(&self, callback: impl Fn(Vec<Value>) -> Value + Send + Sync + 'static) -> Result<IResourceWithEnvironment, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        let callback_id = register_callback(callback);
        args.insert("callback".to_string(), Value::String(callback_id));
        let result = self.client.invoke_capability("Aspire.Hosting/withEnvironmentCallback", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResourceWithEnvironment::new(handle, self.client.clone()))
    }

    /// Sets an environment variable from an endpoint reference
    pub fn with_environment_endpoint(&self, name: &str, endpoint_reference: &EndpointReference) -> Result<IResourceWithEnvironment, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("name".to_string(), serde_json::to_value(&name).unwrap_or(Value::Null));
        args.insert("endpointReference".to_string(), endpoint_reference.handle().to_json());
        let result = self.client.invoke_capability("Aspire.Hosting/withEnvironmentEndpoint", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResourceWithEnvironment::new(handle, self.client.clone()))
    }

    /// Sets an environment variable from a parameter resource
    pub fn with_environment_parameter(&self, name: &str, parameter: &ParameterResource) -> Result<IResourceWithEnvironment, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("name".to_string(), serde_json::to_value(&name).unwrap_or(Value::Null));
        args.insert("parameter".to_string(), parameter.handle().to_json());
        let result = self.client.invoke_capability("Aspire.Hosting/withEnvironmentParameter", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResourceWithEnvironment::new(handle, self.client.clone()))
    }

    /// Sets an environment variable from a connection string resource
    pub fn with_environment_connection_string(&self, env_var_name: &str, resource: &IResourceWithConnectionString) -> Result<IResourceWithEnvironment, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("envVarName".to_string(), serde_json::to_value(&env_var_name).unwrap_or(Value::Null));
        args.insert("resource".to_string(), resource.handle().to_json());
        let result = self.client.invoke_capability("Aspire.Hosting/withEnvironmentConnectionString", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResourceWithEnvironment::new(handle, self.client.clone()))
    }

    /// Adds arguments
    pub fn with_args(&self, args: Vec<String>) -> Result<IResourceWithArgs, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("args".to_string(), serde_json::to_value(&args).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting/withArgs", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResourceWithArgs::new(handle, self.client.clone()))
    }

    /// Sets command-line arguments via callback
    pub fn with_args_callback(&self, callback: impl Fn(Vec<Value>) -> Value + Send + Sync + 'static) -> Result<IResourceWithArgs, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        let callback_id = register_callback(callback);
        args.insert("callback".to_string(), Value::String(callback_id));
        let result = self.client.invoke_capability("Aspire.Hosting/withArgsCallback", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResourceWithArgs::new(handle, self.client.clone()))
    }

    /// Adds a reference to another resource
    pub fn with_reference(&self, source: &IResource, connection_name: Option<&str>, optional: Option<bool>, name: Option<&str>) -> Result<IResourceWithEnvironment, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("source".to_string(), source.handle().to_json());
        if let Some(ref v) = connection_name {
            args.insert("connectionName".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        if let Some(ref v) = optional {
            args.insert("optional".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        if let Some(ref v) = name {
            args.insert("name".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        let result = self.client.invoke_capability("Aspire.Hosting/withReference", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResourceWithEnvironment::new(handle, self.client.clone()))
    }

    /// Adds a reference to a URI
    pub fn with_reference_uri(&self, name: &str, uri: &str) -> Result<IResourceWithEnvironment, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("name".to_string(), serde_json::to_value(&name).unwrap_or(Value::Null));
        args.insert("uri".to_string(), serde_json::to_value(&uri).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting/withReferenceUri", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResourceWithEnvironment::new(handle, self.client.clone()))
    }

    /// Adds a reference to an external service
    pub fn with_reference_external_service(&self, external_service: &ExternalServiceResource) -> Result<IResourceWithEnvironment, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("externalService".to_string(), external_service.handle().to_json());
        let result = self.client.invoke_capability("Aspire.Hosting/withReferenceExternalService", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResourceWithEnvironment::new(handle, self.client.clone()))
    }

    /// Adds a reference to an endpoint
    pub fn with_reference_endpoint(&self, endpoint_reference: &EndpointReference) -> Result<IResourceWithEnvironment, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("endpointReference".to_string(), endpoint_reference.handle().to_json());
        let result = self.client.invoke_capability("Aspire.Hosting/withReferenceEndpoint", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResourceWithEnvironment::new(handle, self.client.clone()))
    }

    /// Adds a network endpoint
    pub fn with_endpoint(&self, port: Option<f64>, target_port: Option<f64>, scheme: Option<&str>, name: Option<&str>, env: Option<&str>, is_proxied: Option<bool>, is_external: Option<bool>, protocol: Option<ProtocolType>) -> Result<IResourceWithEndpoints, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        if let Some(ref v) = port {
            args.insert("port".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        if let Some(ref v) = target_port {
            args.insert("targetPort".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        if let Some(ref v) = scheme {
            args.insert("scheme".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        if let Some(ref v) = name {
            args.insert("name".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        if let Some(ref v) = env {
            args.insert("env".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        if let Some(ref v) = is_proxied {
            args.insert("isProxied".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        if let Some(ref v) = is_external {
            args.insert("isExternal".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        if let Some(ref v) = protocol {
            args.insert("protocol".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        let result = self.client.invoke_capability("Aspire.Hosting/withEndpoint", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResourceWithEndpoints::new(handle, self.client.clone()))
    }

    /// Adds an HTTP endpoint
    pub fn with_http_endpoint(&self, port: Option<f64>, target_port: Option<f64>, name: Option<&str>, env: Option<&str>, is_proxied: Option<bool>) -> Result<IResourceWithEndpoints, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        if let Some(ref v) = port {
            args.insert("port".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        if let Some(ref v) = target_port {
            args.insert("targetPort".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        if let Some(ref v) = name {
            args.insert("name".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        if let Some(ref v) = env {
            args.insert("env".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        if let Some(ref v) = is_proxied {
            args.insert("isProxied".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        let result = self.client.invoke_capability("Aspire.Hosting/withHttpEndpoint", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResourceWithEndpoints::new(handle, self.client.clone()))
    }

    /// Adds an HTTPS endpoint
    pub fn with_https_endpoint(&self, port: Option<f64>, target_port: Option<f64>, name: Option<&str>, env: Option<&str>, is_proxied: Option<bool>) -> Result<IResourceWithEndpoints, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        if let Some(ref v) = port {
            args.insert("port".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        if let Some(ref v) = target_port {
            args.insert("targetPort".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        if let Some(ref v) = name {
            args.insert("name".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        if let Some(ref v) = env {
            args.insert("env".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        if let Some(ref v) = is_proxied {
            args.insert("isProxied".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        let result = self.client.invoke_capability("Aspire.Hosting/withHttpsEndpoint", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResourceWithEndpoints::new(handle, self.client.clone()))
    }

    /// Makes HTTP endpoints externally accessible
    pub fn with_external_http_endpoints(&self) -> Result<IResourceWithEndpoints, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        let result = self.client.invoke_capability("Aspire.Hosting/withExternalHttpEndpoints", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResourceWithEndpoints::new(handle, self.client.clone()))
    }

    /// Gets an endpoint reference
    pub fn get_endpoint(&self, name: &str) -> Result<EndpointReference, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("name".to_string(), serde_json::to_value(&name).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting/getEndpoint", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(EndpointReference::new(handle, self.client.clone()))
    }

    /// Configures resource for HTTP/2
    pub fn as_http2_service(&self) -> Result<IResourceWithEndpoints, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        let result = self.client.invoke_capability("Aspire.Hosting/asHttp2Service", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResourceWithEndpoints::new(handle, self.client.clone()))
    }

    /// Customizes displayed URLs via callback
    pub fn with_urls_callback(&self, callback: impl Fn(Vec<Value>) -> Value + Send + Sync + 'static) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        let callback_id = register_callback(callback);
        args.insert("callback".to_string(), Value::String(callback_id));
        let result = self.client.invoke_capability("Aspire.Hosting/withUrlsCallback", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Adds or modifies displayed URLs
    pub fn with_url(&self, url: &str, display_text: Option<&str>) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("url".to_string(), serde_json::to_value(&url).unwrap_or(Value::Null));
        if let Some(ref v) = display_text {
            args.insert("displayText".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        let result = self.client.invoke_capability("Aspire.Hosting/withUrl", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Adds a URL using a reference expression
    pub fn with_url_expression(&self, url: ReferenceExpression, display_text: Option<&str>) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("url".to_string(), serde_json::to_value(&url).unwrap_or(Value::Null));
        if let Some(ref v) = display_text {
            args.insert("displayText".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        let result = self.client.invoke_capability("Aspire.Hosting/withUrlExpression", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Customizes the URL for a specific endpoint via callback
    pub fn with_url_for_endpoint(&self, endpoint_name: &str, callback: impl Fn(Vec<Value>) -> Value + Send + Sync + 'static) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("endpointName".to_string(), serde_json::to_value(&endpoint_name).unwrap_or(Value::Null));
        let callback_id = register_callback(callback);
        args.insert("callback".to_string(), Value::String(callback_id));
        let result = self.client.invoke_capability("Aspire.Hosting/withUrlForEndpoint", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Adds a URL for a specific endpoint via factory callback
    pub fn with_url_for_endpoint_factory(&self, endpoint_name: &str, callback: impl Fn(Vec<Value>) -> Value + Send + Sync + 'static) -> Result<IResourceWithEndpoints, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("endpointName".to_string(), serde_json::to_value(&endpoint_name).unwrap_or(Value::Null));
        let callback_id = register_callback(callback);
        args.insert("callback".to_string(), Value::String(callback_id));
        let result = self.client.invoke_capability("Aspire.Hosting/withUrlForEndpointFactory", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResourceWithEndpoints::new(handle, self.client.clone()))
    }

    /// Excludes the resource from the deployment manifest
    pub fn exclude_from_manifest(&self) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        let result = self.client.invoke_capability("Aspire.Hosting/excludeFromManifest", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Waits for another resource to be ready
    pub fn wait_for(&self, dependency: &IResource) -> Result<IResourceWithWaitSupport, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("dependency".to_string(), dependency.handle().to_json());
        let result = self.client.invoke_capability("Aspire.Hosting/waitForResource", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResourceWithWaitSupport::new(handle, self.client.clone()))
    }

    /// Waits for another resource with specific behavior
    pub fn wait_for_with_behavior(&self, dependency: &IResource, wait_behavior: WaitBehavior) -> Result<IResourceWithWaitSupport, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("dependency".to_string(), dependency.handle().to_json());
        args.insert("waitBehavior".to_string(), serde_json::to_value(&wait_behavior).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting/waitForWithBehavior", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResourceWithWaitSupport::new(handle, self.client.clone()))
    }

    /// Waits for another resource to start
    pub fn wait_for_start(&self, dependency: &IResource) -> Result<IResourceWithWaitSupport, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("dependency".to_string(), dependency.handle().to_json());
        let result = self.client.invoke_capability("Aspire.Hosting/waitForResourceStart", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResourceWithWaitSupport::new(handle, self.client.clone()))
    }

    /// Waits for another resource to start with specific behavior
    pub fn wait_for_start_with_behavior(&self, dependency: &IResource, wait_behavior: WaitBehavior) -> Result<IResourceWithWaitSupport, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("dependency".to_string(), dependency.handle().to_json());
        args.insert("waitBehavior".to_string(), serde_json::to_value(&wait_behavior).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting/waitForStartWithBehavior", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResourceWithWaitSupport::new(handle, self.client.clone()))
    }

    /// Prevents resource from starting automatically
    pub fn with_explicit_start(&self) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        let result = self.client.invoke_capability("Aspire.Hosting/withExplicitStart", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Waits for resource completion
    pub fn wait_for_completion(&self, dependency: &IResource, exit_code: Option<f64>) -> Result<IResourceWithWaitSupport, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("dependency".to_string(), dependency.handle().to_json());
        if let Some(ref v) = exit_code {
            args.insert("exitCode".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        let result = self.client.invoke_capability("Aspire.Hosting/waitForResourceCompletion", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResourceWithWaitSupport::new(handle, self.client.clone()))
    }

    /// Adds a health check by key
    pub fn with_health_check(&self, key: &str) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("key".to_string(), serde_json::to_value(&key).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting/withHealthCheck", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Adds an HTTP health check
    pub fn with_http_health_check(&self, path: Option<&str>, status_code: Option<f64>, endpoint_name: Option<&str>) -> Result<IResourceWithEndpoints, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        if let Some(ref v) = path {
            args.insert("path".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        if let Some(ref v) = status_code {
            args.insert("statusCode".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        if let Some(ref v) = endpoint_name {
            args.insert("endpointName".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        let result = self.client.invoke_capability("Aspire.Hosting/withHttpHealthCheck", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResourceWithEndpoints::new(handle, self.client.clone()))
    }

    /// Adds a resource command
    pub fn with_command(&self, name: &str, display_name: &str, execute_command: impl Fn(Vec<Value>) -> Value + Send + Sync + 'static, command_options: Option<CommandOptions>) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("name".to_string(), serde_json::to_value(&name).unwrap_or(Value::Null));
        args.insert("displayName".to_string(), serde_json::to_value(&display_name).unwrap_or(Value::Null));
        let callback_id = register_callback(execute_command);
        args.insert("executeCommand".to_string(), Value::String(callback_id));
        if let Some(ref v) = command_options {
            args.insert("commandOptions".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        let result = self.client.invoke_capability("Aspire.Hosting/withCommand", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Configures developer certificate trust
    pub fn with_developer_certificate_trust(&self, trust: bool) -> Result<IResourceWithEnvironment, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("trust".to_string(), serde_json::to_value(&trust).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting/withDeveloperCertificateTrust", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResourceWithEnvironment::new(handle, self.client.clone()))
    }

    /// Sets the certificate trust scope
    pub fn with_certificate_trust_scope(&self, scope: CertificateTrustScope) -> Result<IResourceWithEnvironment, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("scope".to_string(), serde_json::to_value(&scope).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting/withCertificateTrustScope", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResourceWithEnvironment::new(handle, self.client.clone()))
    }

    /// Configures HTTPS with a developer certificate
    pub fn with_https_developer_certificate(&self, password: Option<&ParameterResource>) -> Result<IResourceWithEnvironment, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        if let Some(ref v) = password {
            args.insert("password".to_string(), v.handle().to_json());
        }
        let result = self.client.invoke_capability("Aspire.Hosting/withParameterHttpsDeveloperCertificate", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResourceWithEnvironment::new(handle, self.client.clone()))
    }

    /// Removes HTTPS certificate configuration
    pub fn without_https_certificate(&self) -> Result<IResourceWithEnvironment, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        let result = self.client.invoke_capability("Aspire.Hosting/withoutHttpsCertificate", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResourceWithEnvironment::new(handle, self.client.clone()))
    }

    /// Sets the parent relationship
    pub fn with_parent_relationship(&self, parent: &IResource) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("parent".to_string(), parent.handle().to_json());
        let result = self.client.invoke_capability("Aspire.Hosting/withBuilderParentRelationship", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Sets a child relationship
    pub fn with_child_relationship(&self, child: &IResource) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("child".to_string(), child.handle().to_json());
        let result = self.client.invoke_capability("Aspire.Hosting/withBuilderChildRelationship", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Sets the icon for the resource
    pub fn with_icon_name(&self, icon_name: &str, icon_variant: Option<IconVariant>) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("iconName".to_string(), serde_json::to_value(&icon_name).unwrap_or(Value::Null));
        if let Some(ref v) = icon_variant {
            args.insert("iconVariant".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        let result = self.client.invoke_capability("Aspire.Hosting/withIconName", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Adds an HTTP health probe to the resource
    pub fn with_http_probe(&self, probe_type: ProbeType, path: Option<&str>, initial_delay_seconds: Option<f64>, period_seconds: Option<f64>, timeout_seconds: Option<f64>, failure_threshold: Option<f64>, success_threshold: Option<f64>, endpoint_name: Option<&str>) -> Result<IResourceWithEndpoints, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("probeType".to_string(), serde_json::to_value(&probe_type).unwrap_or(Value::Null));
        if let Some(ref v) = path {
            args.insert("path".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        if let Some(ref v) = initial_delay_seconds {
            args.insert("initialDelaySeconds".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        if let Some(ref v) = period_seconds {
            args.insert("periodSeconds".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        if let Some(ref v) = timeout_seconds {
            args.insert("timeoutSeconds".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        if let Some(ref v) = failure_threshold {
            args.insert("failureThreshold".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        if let Some(ref v) = success_threshold {
            args.insert("successThreshold".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        if let Some(ref v) = endpoint_name {
            args.insert("endpointName".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        let result = self.client.invoke_capability("Aspire.Hosting/withHttpProbe", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResourceWithEndpoints::new(handle, self.client.clone()))
    }

    /// Excludes the resource from MCP server exposure
    pub fn exclude_from_mcp(&self) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        let result = self.client.invoke_capability("Aspire.Hosting/excludeFromMcp", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Sets the remote image name for publishing
    pub fn with_remote_image_name(&self, remote_image_name: &str) -> Result<IComputeResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("remoteImageName".to_string(), serde_json::to_value(&remote_image_name).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting/withRemoteImageName", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IComputeResource::new(handle, self.client.clone()))
    }

    /// Sets the remote image tag for publishing
    pub fn with_remote_image_tag(&self, remote_image_tag: &str) -> Result<IComputeResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("remoteImageTag".to_string(), serde_json::to_value(&remote_image_tag).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting/withRemoteImageTag", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IComputeResource::new(handle, self.client.clone()))
    }

    /// Adds a pipeline step to the resource
    pub fn with_pipeline_step_factory(&self, step_name: &str, callback: impl Fn(Vec<Value>) -> Value + Send + Sync + 'static, depends_on: Option<Vec<String>>, required_by: Option<Vec<String>>, tags: Option<Vec<String>>, description: Option<&str>) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("stepName".to_string(), serde_json::to_value(&step_name).unwrap_or(Value::Null));
        let callback_id = register_callback(callback);
        args.insert("callback".to_string(), Value::String(callback_id));
        if let Some(ref v) = depends_on {
            args.insert("dependsOn".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        if let Some(ref v) = required_by {
            args.insert("requiredBy".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        if let Some(ref v) = tags {
            args.insert("tags".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        if let Some(ref v) = description {
            args.insert("description".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        let result = self.client.invoke_capability("Aspire.Hosting/withPipelineStepFactory", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Configures pipeline step dependencies via a callback
    pub fn with_pipeline_configuration(&self, callback: impl Fn(Vec<Value>) -> Value + Send + Sync + 'static) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        let callback_id = register_callback(callback);
        args.insert("callback".to_string(), Value::String(callback_id));
        let result = self.client.invoke_capability("Aspire.Hosting/withPipelineConfiguration", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Adds a volume
    pub fn with_volume(&self, target: &str, name: Option<&str>, is_read_only: Option<bool>) -> Result<ContainerResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("resource".to_string(), self.handle.to_json());
        args.insert("target".to_string(), serde_json::to_value(&target).unwrap_or(Value::Null));
        if let Some(ref v) = name {
            args.insert("name".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        if let Some(ref v) = is_read_only {
            args.insert("isReadOnly".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        let result = self.client.invoke_capability("Aspire.Hosting/withVolume", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(ContainerResource::new(handle, self.client.clone()))
    }

    /// Gets the resource name
    pub fn get_resource_name(&self) -> Result<String, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("resource".to_string(), self.handle.to_json());
        let result = self.client.invoke_capability("Aspire.Hosting/getResourceName", args)?;
        Ok(serde_json::from_value(result)?)
    }

    /// Subscribes to the BeforeResourceStarted event
    pub fn on_before_resource_started(&self, callback: impl Fn(Vec<Value>) -> Value + Send + Sync + 'static) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        let callback_id = register_callback(callback);
        args.insert("callback".to_string(), Value::String(callback_id));
        let result = self.client.invoke_capability("Aspire.Hosting/onBeforeResourceStarted", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Subscribes to the ResourceStopped event
    pub fn on_resource_stopped(&self, callback: impl Fn(Vec<Value>) -> Value + Send + Sync + 'static) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        let callback_id = register_callback(callback);
        args.insert("callback".to_string(), Value::String(callback_id));
        let result = self.client.invoke_capability("Aspire.Hosting/onResourceStopped", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Subscribes to the InitializeResource event
    pub fn on_initialize_resource(&self, callback: impl Fn(Vec<Value>) -> Value + Send + Sync + 'static) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        let callback_id = register_callback(callback);
        args.insert("callback".to_string(), Value::String(callback_id));
        let result = self.client.invoke_capability("Aspire.Hosting/onInitializeResource", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Subscribes to the ResourceEndpointsAllocated event
    pub fn on_resource_endpoints_allocated(&self, callback: impl Fn(Vec<Value>) -> Value + Send + Sync + 'static) -> Result<IResourceWithEndpoints, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        let callback_id = register_callback(callback);
        args.insert("callback".to_string(), Value::String(callback_id));
        let result = self.client.invoke_capability("Aspire.Hosting/onResourceEndpointsAllocated", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResourceWithEndpoints::new(handle, self.client.clone()))
    }

    /// Subscribes to the ResourceReady event
    pub fn on_resource_ready(&self, callback: impl Fn(Vec<Value>) -> Value + Send + Sync + 'static) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        let callback_id = register_callback(callback);
        args.insert("callback".to_string(), Value::String(callback_id));
        let result = self.client.invoke_capability("Aspire.Hosting/onResourceReady", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
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

    /// Adds a dependency from a string or another resource
    pub fn with_union_dependency(&self, dependency: Value) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("dependency".to_string(), serde_json::to_value(&dependency).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting.CodeGeneration.Rust.Tests/withUnionDependency", args)?;
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

    /// Adds a label to the resource
    pub fn with_merge_label(&self, label: &str) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("label".to_string(), serde_json::to_value(&label).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting.CodeGeneration.Rust.Tests/withMergeLabel", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Adds a categorized label to the resource
    pub fn with_merge_label_categorized(&self, label: &str, category: &str) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("label".to_string(), serde_json::to_value(&label).unwrap_or(Value::Null));
        args.insert("category".to_string(), serde_json::to_value(&category).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting.CodeGeneration.Rust.Tests/withMergeLabelCategorized", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Configures a named endpoint
    pub fn with_merge_endpoint(&self, endpoint_name: &str, port: f64) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("endpointName".to_string(), serde_json::to_value(&endpoint_name).unwrap_or(Value::Null));
        args.insert("port".to_string(), serde_json::to_value(&port).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting.CodeGeneration.Rust.Tests/withMergeEndpoint", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Configures a named endpoint with scheme
    pub fn with_merge_endpoint_scheme(&self, endpoint_name: &str, port: f64, scheme: &str) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("endpointName".to_string(), serde_json::to_value(&endpoint_name).unwrap_or(Value::Null));
        args.insert("port".to_string(), serde_json::to_value(&port).unwrap_or(Value::Null));
        args.insert("scheme".to_string(), serde_json::to_value(&scheme).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting.CodeGeneration.Rust.Tests/withMergeEndpointScheme", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Configures resource logging
    pub fn with_merge_logging(&self, log_level: &str, enable_console: Option<bool>, max_files: Option<f64>) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("logLevel".to_string(), serde_json::to_value(&log_level).unwrap_or(Value::Null));
        if let Some(ref v) = enable_console {
            args.insert("enableConsole".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        if let Some(ref v) = max_files {
            args.insert("maxFiles".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        let result = self.client.invoke_capability("Aspire.Hosting.CodeGeneration.Rust.Tests/withMergeLogging", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Configures resource logging with file path
    pub fn with_merge_logging_path(&self, log_level: &str, log_path: &str, enable_console: Option<bool>, max_files: Option<f64>) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("logLevel".to_string(), serde_json::to_value(&log_level).unwrap_or(Value::Null));
        args.insert("logPath".to_string(), serde_json::to_value(&log_path).unwrap_or(Value::Null));
        if let Some(ref v) = enable_console {
            args.insert("enableConsole".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        if let Some(ref v) = max_files {
            args.insert("maxFiles".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        let result = self.client.invoke_capability("Aspire.Hosting.CodeGeneration.Rust.Tests/withMergeLoggingPath", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Configures a route
    pub fn with_merge_route(&self, path: &str, method: &str, handler: &str, priority: f64) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("path".to_string(), serde_json::to_value(&path).unwrap_or(Value::Null));
        args.insert("method".to_string(), serde_json::to_value(&method).unwrap_or(Value::Null));
        args.insert("handler".to_string(), serde_json::to_value(&handler).unwrap_or(Value::Null));
        args.insert("priority".to_string(), serde_json::to_value(&priority).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting.CodeGeneration.Rust.Tests/withMergeRoute", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Configures a route with middleware
    pub fn with_merge_route_middleware(&self, path: &str, method: &str, handler: &str, priority: f64, middleware: &str) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("path".to_string(), serde_json::to_value(&path).unwrap_or(Value::Null));
        args.insert("method".to_string(), serde_json::to_value(&method).unwrap_or(Value::Null));
        args.insert("handler".to_string(), serde_json::to_value(&handler).unwrap_or(Value::Null));
        args.insert("priority".to_string(), serde_json::to_value(&priority).unwrap_or(Value::Null));
        args.insert("middleware".to_string(), serde_json::to_value(&middleware).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting.CodeGeneration.Rust.Tests/withMergeRouteMiddleware", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }
}

/// Wrapper for Aspire.Hosting/Aspire.Hosting.DistributedApplication
pub struct DistributedApplication {
    handle: Handle,
    client: Arc<AspireClient>,
}

impl HasHandle for DistributedApplication {
    fn handle(&self) -> &Handle {
        &self.handle
    }
}

impl DistributedApplication {
    pub fn new(handle: Handle, client: Arc<AspireClient>) -> Self {
        Self { handle, client }
    }

    pub fn handle(&self) -> &Handle {
        &self.handle
    }

    pub fn client(&self) -> &Arc<AspireClient> {
        &self.client
    }

    /// Runs the distributed application
    pub fn run(&self, cancellation_token: Option<&CancellationToken>) -> Result<(), Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("context".to_string(), self.handle.to_json());
        if let Some(token) = cancellation_token {
            let token_id = register_cancellation(token, self.client.clone());
            args.insert("cancellationToken".to_string(), Value::String(token_id));
        }
        let result = self.client.invoke_capability("Aspire.Hosting/run", args)?;
        Ok(())
    }
}

/// Wrapper for Aspire.Hosting/Aspire.Hosting.Eventing.DistributedApplicationEventSubscription
pub struct DistributedApplicationEventSubscription {
    handle: Handle,
    client: Arc<AspireClient>,
}

impl HasHandle for DistributedApplicationEventSubscription {
    fn handle(&self) -> &Handle {
        &self.handle
    }
}

impl DistributedApplicationEventSubscription {
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

/// Wrapper for Aspire.Hosting/Aspire.Hosting.DistributedApplicationExecutionContext
pub struct DistributedApplicationExecutionContext {
    handle: Handle,
    client: Arc<AspireClient>,
}

impl HasHandle for DistributedApplicationExecutionContext {
    fn handle(&self) -> &Handle {
        &self.handle
    }
}

impl DistributedApplicationExecutionContext {
    pub fn new(handle: Handle, client: Arc<AspireClient>) -> Self {
        Self { handle, client }
    }

    pub fn handle(&self) -> &Handle {
        &self.handle
    }

    pub fn client(&self) -> &Arc<AspireClient> {
        &self.client
    }

    /// Gets the PublisherName property
    pub fn publisher_name(&self) -> Result<String, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("context".to_string(), self.handle.to_json());
        let result = self.client.invoke_capability("Aspire.Hosting/DistributedApplicationExecutionContext.publisherName", args)?;
        Ok(serde_json::from_value(result)?)
    }

    /// Sets the PublisherName property
    pub fn set_publisher_name(&self, value: &str) -> Result<DistributedApplicationExecutionContext, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("context".to_string(), self.handle.to_json());
        args.insert("value".to_string(), serde_json::to_value(&value).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting/DistributedApplicationExecutionContext.setPublisherName", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(DistributedApplicationExecutionContext::new(handle, self.client.clone()))
    }

    /// Gets the Operation property
    pub fn operation(&self) -> Result<DistributedApplicationOperation, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("context".to_string(), self.handle.to_json());
        let result = self.client.invoke_capability("Aspire.Hosting/DistributedApplicationExecutionContext.operation", args)?;
        Ok(serde_json::from_value(result)?)
    }

    /// Gets the ServiceProvider property
    pub fn service_provider(&self) -> Result<IServiceProvider, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("context".to_string(), self.handle.to_json());
        let result = self.client.invoke_capability("Aspire.Hosting/DistributedApplicationExecutionContext.serviceProvider", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IServiceProvider::new(handle, self.client.clone()))
    }

    /// Gets the IsPublishMode property
    pub fn is_publish_mode(&self) -> Result<bool, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("context".to_string(), self.handle.to_json());
        let result = self.client.invoke_capability("Aspire.Hosting/DistributedApplicationExecutionContext.isPublishMode", args)?;
        Ok(serde_json::from_value(result)?)
    }

    /// Gets the IsRunMode property
    pub fn is_run_mode(&self) -> Result<bool, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("context".to_string(), self.handle.to_json());
        let result = self.client.invoke_capability("Aspire.Hosting/DistributedApplicationExecutionContext.isRunMode", args)?;
        Ok(serde_json::from_value(result)?)
    }
}

/// Wrapper for Aspire.Hosting/Aspire.Hosting.DistributedApplicationExecutionContextOptions
pub struct DistributedApplicationExecutionContextOptions {
    handle: Handle,
    client: Arc<AspireClient>,
}

impl HasHandle for DistributedApplicationExecutionContextOptions {
    fn handle(&self) -> &Handle {
        &self.handle
    }
}

impl DistributedApplicationExecutionContextOptions {
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

/// Wrapper for Aspire.Hosting/Aspire.Hosting.ApplicationModel.DistributedApplicationModel
pub struct DistributedApplicationModel {
    handle: Handle,
    client: Arc<AspireClient>,
}

impl HasHandle for DistributedApplicationModel {
    fn handle(&self) -> &Handle {
        &self.handle
    }
}

impl DistributedApplicationModel {
    pub fn new(handle: Handle, client: Arc<AspireClient>) -> Self {
        Self { handle, client }
    }

    pub fn handle(&self) -> &Handle {
        &self.handle
    }

    pub fn client(&self) -> &Arc<AspireClient> {
        &self.client
    }

    /// Gets resources from the distributed application model
    pub fn get_resources(&self) -> Result<Vec<IResource>, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("model".to_string(), self.handle.to_json());
        let result = self.client.invoke_capability("Aspire.Hosting/getResources", args)?;
        let handles: Vec<Handle> = serde_json::from_value(result)?;
        Ok(handles.into_iter().map(|h| IResource::new(h, self.client.clone())).collect())
    }

    /// Finds a resource by name
    pub fn find_resource_by_name(&self, name: &str) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("model".to_string(), self.handle.to_json());
        args.insert("name".to_string(), serde_json::to_value(&name).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting/findResourceByName", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }
}

/// Wrapper for Aspire.Hosting/Aspire.Hosting.Eventing.DistributedApplicationResourceEventSubscription
pub struct DistributedApplicationResourceEventSubscription {
    handle: Handle,
    client: Arc<AspireClient>,
}

impl HasHandle for DistributedApplicationResourceEventSubscription {
    fn handle(&self) -> &Handle {
        &self.handle
    }
}

impl DistributedApplicationResourceEventSubscription {
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

/// Wrapper for Aspire.Hosting/Aspire.Hosting.ApplicationModel.DotnetToolResource
pub struct DotnetToolResource {
    handle: Handle,
    client: Arc<AspireClient>,
}

impl HasHandle for DotnetToolResource {
    fn handle(&self) -> &Handle {
        &self.handle
    }
}

impl DotnetToolResource {
    pub fn new(handle: Handle, client: Arc<AspireClient>) -> Self {
        Self { handle, client }
    }

    pub fn handle(&self) -> &Handle {
        &self.handle
    }

    pub fn client(&self) -> &Arc<AspireClient> {
        &self.client
    }

    /// Configures a resource to use a container registry
    pub fn with_container_registry(&self, registry: &IResource) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("registry".to_string(), registry.handle().to_json());
        let result = self.client.invoke_capability("Aspire.Hosting/withContainerRegistry", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Sets the base image for a Dockerfile build
    pub fn with_dockerfile_base_image(&self, build_image: Option<&str>, runtime_image: Option<&str>) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        if let Some(ref v) = build_image {
            args.insert("buildImage".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        if let Some(ref v) = runtime_image {
            args.insert("runtimeImage".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        let result = self.client.invoke_capability("Aspire.Hosting/withDockerfileBaseImage", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Sets the tool package ID
    pub fn with_tool_package(&self, package_id: &str) -> Result<DotnetToolResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("packageId".to_string(), serde_json::to_value(&package_id).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting/withToolPackage", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(DotnetToolResource::new(handle, self.client.clone()))
    }

    /// Sets the tool version
    pub fn with_tool_version(&self, version: &str) -> Result<DotnetToolResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("version".to_string(), serde_json::to_value(&version).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting/withToolVersion", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(DotnetToolResource::new(handle, self.client.clone()))
    }

    /// Allows prerelease tool versions
    pub fn with_tool_prerelease(&self) -> Result<DotnetToolResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        let result = self.client.invoke_capability("Aspire.Hosting/withToolPrerelease", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(DotnetToolResource::new(handle, self.client.clone()))
    }

    /// Adds a NuGet source for the tool
    pub fn with_tool_source(&self, source: &str) -> Result<DotnetToolResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("source".to_string(), serde_json::to_value(&source).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting/withToolSource", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(DotnetToolResource::new(handle, self.client.clone()))
    }

    /// Ignores existing NuGet feeds
    pub fn with_tool_ignore_existing_feeds(&self) -> Result<DotnetToolResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        let result = self.client.invoke_capability("Aspire.Hosting/withToolIgnoreExistingFeeds", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(DotnetToolResource::new(handle, self.client.clone()))
    }

    /// Ignores failed NuGet sources
    pub fn with_tool_ignore_failed_sources(&self) -> Result<DotnetToolResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        let result = self.client.invoke_capability("Aspire.Hosting/withToolIgnoreFailedSources", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(DotnetToolResource::new(handle, self.client.clone()))
    }

    /// Publishes the executable as a Docker container
    pub fn publish_as_docker_file(&self) -> Result<ExecutableResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        let result = self.client.invoke_capability("Aspire.Hosting/publishAsDockerFile", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(ExecutableResource::new(handle, self.client.clone()))
    }

    /// Publishes an executable as a Docker file with optional container configuration
    pub fn publish_as_docker_file_with_configure(&self, configure: impl Fn(Vec<Value>) -> Value + Send + Sync + 'static) -> Result<ExecutableResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        let callback_id = register_callback(configure);
        args.insert("configure".to_string(), Value::String(callback_id));
        let result = self.client.invoke_capability("Aspire.Hosting/publishAsDockerFileWithConfigure", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(ExecutableResource::new(handle, self.client.clone()))
    }

    /// Sets the executable command
    pub fn with_executable_command(&self, command: &str) -> Result<ExecutableResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("command".to_string(), serde_json::to_value(&command).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting/withExecutableCommand", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(ExecutableResource::new(handle, self.client.clone()))
    }

    /// Sets the executable working directory
    pub fn with_working_directory(&self, working_directory: &str) -> Result<ExecutableResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("workingDirectory".to_string(), serde_json::to_value(&working_directory).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting/withWorkingDirectory", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(ExecutableResource::new(handle, self.client.clone()))
    }

    /// Configures an MCP server endpoint on the resource
    pub fn with_mcp_server(&self, path: Option<&str>, endpoint_name: Option<&str>) -> Result<IResourceWithEndpoints, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        if let Some(ref v) = path {
            args.insert("path".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        if let Some(ref v) = endpoint_name {
            args.insert("endpointName".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        let result = self.client.invoke_capability("Aspire.Hosting/withMcpServer", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResourceWithEndpoints::new(handle, self.client.clone()))
    }

    /// Configures OTLP telemetry export
    pub fn with_otlp_exporter(&self) -> Result<IResourceWithEnvironment, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        let result = self.client.invoke_capability("Aspire.Hosting/withOtlpExporter", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResourceWithEnvironment::new(handle, self.client.clone()))
    }

    /// Configures OTLP telemetry export with specific protocol
    pub fn with_otlp_exporter_protocol(&self, protocol: OtlpProtocol) -> Result<IResourceWithEnvironment, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("protocol".to_string(), serde_json::to_value(&protocol).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting/withOtlpExporterProtocol", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResourceWithEnvironment::new(handle, self.client.clone()))
    }

    /// Adds a required command dependency
    pub fn with_required_command(&self, command: &str, help_link: Option<&str>) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("command".to_string(), serde_json::to_value(&command).unwrap_or(Value::Null));
        if let Some(ref v) = help_link {
            args.insert("helpLink".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        let result = self.client.invoke_capability("Aspire.Hosting/withRequiredCommand", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Sets an environment variable
    pub fn with_environment(&self, name: &str, value: Value) -> Result<IResourceWithEnvironment, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("name".to_string(), serde_json::to_value(&name).unwrap_or(Value::Null));
        args.insert("value".to_string(), serde_json::to_value(&value).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting/withEnvironment", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResourceWithEnvironment::new(handle, self.client.clone()))
    }

    /// Sets an environment variable from a reference expression
    pub fn with_environment_expression(&self, name: &str, value: ReferenceExpression) -> Result<IResourceWithEnvironment, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("name".to_string(), serde_json::to_value(&name).unwrap_or(Value::Null));
        args.insert("value".to_string(), serde_json::to_value(&value).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting/withEnvironmentExpression", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResourceWithEnvironment::new(handle, self.client.clone()))
    }

    /// Sets environment variables via callback
    pub fn with_environment_callback(&self, callback: impl Fn(Vec<Value>) -> Value + Send + Sync + 'static) -> Result<IResourceWithEnvironment, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        let callback_id = register_callback(callback);
        args.insert("callback".to_string(), Value::String(callback_id));
        let result = self.client.invoke_capability("Aspire.Hosting/withEnvironmentCallback", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResourceWithEnvironment::new(handle, self.client.clone()))
    }

    /// Sets an environment variable from an endpoint reference
    pub fn with_environment_endpoint(&self, name: &str, endpoint_reference: &EndpointReference) -> Result<IResourceWithEnvironment, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("name".to_string(), serde_json::to_value(&name).unwrap_or(Value::Null));
        args.insert("endpointReference".to_string(), endpoint_reference.handle().to_json());
        let result = self.client.invoke_capability("Aspire.Hosting/withEnvironmentEndpoint", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResourceWithEnvironment::new(handle, self.client.clone()))
    }

    /// Sets an environment variable from a parameter resource
    pub fn with_environment_parameter(&self, name: &str, parameter: &ParameterResource) -> Result<IResourceWithEnvironment, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("name".to_string(), serde_json::to_value(&name).unwrap_or(Value::Null));
        args.insert("parameter".to_string(), parameter.handle().to_json());
        let result = self.client.invoke_capability("Aspire.Hosting/withEnvironmentParameter", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResourceWithEnvironment::new(handle, self.client.clone()))
    }

    /// Sets an environment variable from a connection string resource
    pub fn with_environment_connection_string(&self, env_var_name: &str, resource: &IResourceWithConnectionString) -> Result<IResourceWithEnvironment, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("envVarName".to_string(), serde_json::to_value(&env_var_name).unwrap_or(Value::Null));
        args.insert("resource".to_string(), resource.handle().to_json());
        let result = self.client.invoke_capability("Aspire.Hosting/withEnvironmentConnectionString", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResourceWithEnvironment::new(handle, self.client.clone()))
    }

    /// Adds arguments
    pub fn with_args(&self, args: Vec<String>) -> Result<IResourceWithArgs, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("args".to_string(), serde_json::to_value(&args).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting/withArgs", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResourceWithArgs::new(handle, self.client.clone()))
    }

    /// Sets command-line arguments via callback
    pub fn with_args_callback(&self, callback: impl Fn(Vec<Value>) -> Value + Send + Sync + 'static) -> Result<IResourceWithArgs, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        let callback_id = register_callback(callback);
        args.insert("callback".to_string(), Value::String(callback_id));
        let result = self.client.invoke_capability("Aspire.Hosting/withArgsCallback", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResourceWithArgs::new(handle, self.client.clone()))
    }

    /// Adds a reference to another resource
    pub fn with_reference(&self, source: &IResource, connection_name: Option<&str>, optional: Option<bool>, name: Option<&str>) -> Result<IResourceWithEnvironment, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("source".to_string(), source.handle().to_json());
        if let Some(ref v) = connection_name {
            args.insert("connectionName".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        if let Some(ref v) = optional {
            args.insert("optional".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        if let Some(ref v) = name {
            args.insert("name".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        let result = self.client.invoke_capability("Aspire.Hosting/withReference", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResourceWithEnvironment::new(handle, self.client.clone()))
    }

    /// Adds a reference to a URI
    pub fn with_reference_uri(&self, name: &str, uri: &str) -> Result<IResourceWithEnvironment, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("name".to_string(), serde_json::to_value(&name).unwrap_or(Value::Null));
        args.insert("uri".to_string(), serde_json::to_value(&uri).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting/withReferenceUri", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResourceWithEnvironment::new(handle, self.client.clone()))
    }

    /// Adds a reference to an external service
    pub fn with_reference_external_service(&self, external_service: &ExternalServiceResource) -> Result<IResourceWithEnvironment, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("externalService".to_string(), external_service.handle().to_json());
        let result = self.client.invoke_capability("Aspire.Hosting/withReferenceExternalService", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResourceWithEnvironment::new(handle, self.client.clone()))
    }

    /// Adds a reference to an endpoint
    pub fn with_reference_endpoint(&self, endpoint_reference: &EndpointReference) -> Result<IResourceWithEnvironment, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("endpointReference".to_string(), endpoint_reference.handle().to_json());
        let result = self.client.invoke_capability("Aspire.Hosting/withReferenceEndpoint", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResourceWithEnvironment::new(handle, self.client.clone()))
    }

    /// Adds a network endpoint
    pub fn with_endpoint(&self, port: Option<f64>, target_port: Option<f64>, scheme: Option<&str>, name: Option<&str>, env: Option<&str>, is_proxied: Option<bool>, is_external: Option<bool>, protocol: Option<ProtocolType>) -> Result<IResourceWithEndpoints, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        if let Some(ref v) = port {
            args.insert("port".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        if let Some(ref v) = target_port {
            args.insert("targetPort".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        if let Some(ref v) = scheme {
            args.insert("scheme".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        if let Some(ref v) = name {
            args.insert("name".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        if let Some(ref v) = env {
            args.insert("env".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        if let Some(ref v) = is_proxied {
            args.insert("isProxied".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        if let Some(ref v) = is_external {
            args.insert("isExternal".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        if let Some(ref v) = protocol {
            args.insert("protocol".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        let result = self.client.invoke_capability("Aspire.Hosting/withEndpoint", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResourceWithEndpoints::new(handle, self.client.clone()))
    }

    /// Adds an HTTP endpoint
    pub fn with_http_endpoint(&self, port: Option<f64>, target_port: Option<f64>, name: Option<&str>, env: Option<&str>, is_proxied: Option<bool>) -> Result<IResourceWithEndpoints, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        if let Some(ref v) = port {
            args.insert("port".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        if let Some(ref v) = target_port {
            args.insert("targetPort".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        if let Some(ref v) = name {
            args.insert("name".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        if let Some(ref v) = env {
            args.insert("env".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        if let Some(ref v) = is_proxied {
            args.insert("isProxied".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        let result = self.client.invoke_capability("Aspire.Hosting/withHttpEndpoint", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResourceWithEndpoints::new(handle, self.client.clone()))
    }

    /// Adds an HTTPS endpoint
    pub fn with_https_endpoint(&self, port: Option<f64>, target_port: Option<f64>, name: Option<&str>, env: Option<&str>, is_proxied: Option<bool>) -> Result<IResourceWithEndpoints, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        if let Some(ref v) = port {
            args.insert("port".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        if let Some(ref v) = target_port {
            args.insert("targetPort".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        if let Some(ref v) = name {
            args.insert("name".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        if let Some(ref v) = env {
            args.insert("env".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        if let Some(ref v) = is_proxied {
            args.insert("isProxied".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        let result = self.client.invoke_capability("Aspire.Hosting/withHttpsEndpoint", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResourceWithEndpoints::new(handle, self.client.clone()))
    }

    /// Makes HTTP endpoints externally accessible
    pub fn with_external_http_endpoints(&self) -> Result<IResourceWithEndpoints, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        let result = self.client.invoke_capability("Aspire.Hosting/withExternalHttpEndpoints", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResourceWithEndpoints::new(handle, self.client.clone()))
    }

    /// Gets an endpoint reference
    pub fn get_endpoint(&self, name: &str) -> Result<EndpointReference, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("name".to_string(), serde_json::to_value(&name).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting/getEndpoint", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(EndpointReference::new(handle, self.client.clone()))
    }

    /// Configures resource for HTTP/2
    pub fn as_http2_service(&self) -> Result<IResourceWithEndpoints, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        let result = self.client.invoke_capability("Aspire.Hosting/asHttp2Service", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResourceWithEndpoints::new(handle, self.client.clone()))
    }

    /// Customizes displayed URLs via callback
    pub fn with_urls_callback(&self, callback: impl Fn(Vec<Value>) -> Value + Send + Sync + 'static) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        let callback_id = register_callback(callback);
        args.insert("callback".to_string(), Value::String(callback_id));
        let result = self.client.invoke_capability("Aspire.Hosting/withUrlsCallback", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Adds or modifies displayed URLs
    pub fn with_url(&self, url: &str, display_text: Option<&str>) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("url".to_string(), serde_json::to_value(&url).unwrap_or(Value::Null));
        if let Some(ref v) = display_text {
            args.insert("displayText".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        let result = self.client.invoke_capability("Aspire.Hosting/withUrl", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Adds a URL using a reference expression
    pub fn with_url_expression(&self, url: ReferenceExpression, display_text: Option<&str>) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("url".to_string(), serde_json::to_value(&url).unwrap_or(Value::Null));
        if let Some(ref v) = display_text {
            args.insert("displayText".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        let result = self.client.invoke_capability("Aspire.Hosting/withUrlExpression", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Customizes the URL for a specific endpoint via callback
    pub fn with_url_for_endpoint(&self, endpoint_name: &str, callback: impl Fn(Vec<Value>) -> Value + Send + Sync + 'static) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("endpointName".to_string(), serde_json::to_value(&endpoint_name).unwrap_or(Value::Null));
        let callback_id = register_callback(callback);
        args.insert("callback".to_string(), Value::String(callback_id));
        let result = self.client.invoke_capability("Aspire.Hosting/withUrlForEndpoint", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Adds a URL for a specific endpoint via factory callback
    pub fn with_url_for_endpoint_factory(&self, endpoint_name: &str, callback: impl Fn(Vec<Value>) -> Value + Send + Sync + 'static) -> Result<IResourceWithEndpoints, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("endpointName".to_string(), serde_json::to_value(&endpoint_name).unwrap_or(Value::Null));
        let callback_id = register_callback(callback);
        args.insert("callback".to_string(), Value::String(callback_id));
        let result = self.client.invoke_capability("Aspire.Hosting/withUrlForEndpointFactory", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResourceWithEndpoints::new(handle, self.client.clone()))
    }

    /// Excludes the resource from the deployment manifest
    pub fn exclude_from_manifest(&self) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        let result = self.client.invoke_capability("Aspire.Hosting/excludeFromManifest", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Waits for another resource to be ready
    pub fn wait_for(&self, dependency: &IResource) -> Result<IResourceWithWaitSupport, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("dependency".to_string(), dependency.handle().to_json());
        let result = self.client.invoke_capability("Aspire.Hosting/waitForResource", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResourceWithWaitSupport::new(handle, self.client.clone()))
    }

    /// Waits for another resource with specific behavior
    pub fn wait_for_with_behavior(&self, dependency: &IResource, wait_behavior: WaitBehavior) -> Result<IResourceWithWaitSupport, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("dependency".to_string(), dependency.handle().to_json());
        args.insert("waitBehavior".to_string(), serde_json::to_value(&wait_behavior).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting/waitForWithBehavior", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResourceWithWaitSupport::new(handle, self.client.clone()))
    }

    /// Waits for another resource to start
    pub fn wait_for_start(&self, dependency: &IResource) -> Result<IResourceWithWaitSupport, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("dependency".to_string(), dependency.handle().to_json());
        let result = self.client.invoke_capability("Aspire.Hosting/waitForResourceStart", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResourceWithWaitSupport::new(handle, self.client.clone()))
    }

    /// Waits for another resource to start with specific behavior
    pub fn wait_for_start_with_behavior(&self, dependency: &IResource, wait_behavior: WaitBehavior) -> Result<IResourceWithWaitSupport, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("dependency".to_string(), dependency.handle().to_json());
        args.insert("waitBehavior".to_string(), serde_json::to_value(&wait_behavior).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting/waitForStartWithBehavior", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResourceWithWaitSupport::new(handle, self.client.clone()))
    }

    /// Prevents resource from starting automatically
    pub fn with_explicit_start(&self) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        let result = self.client.invoke_capability("Aspire.Hosting/withExplicitStart", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Waits for resource completion
    pub fn wait_for_completion(&self, dependency: &IResource, exit_code: Option<f64>) -> Result<IResourceWithWaitSupport, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("dependency".to_string(), dependency.handle().to_json());
        if let Some(ref v) = exit_code {
            args.insert("exitCode".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        let result = self.client.invoke_capability("Aspire.Hosting/waitForResourceCompletion", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResourceWithWaitSupport::new(handle, self.client.clone()))
    }

    /// Adds a health check by key
    pub fn with_health_check(&self, key: &str) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("key".to_string(), serde_json::to_value(&key).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting/withHealthCheck", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Adds an HTTP health check
    pub fn with_http_health_check(&self, path: Option<&str>, status_code: Option<f64>, endpoint_name: Option<&str>) -> Result<IResourceWithEndpoints, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        if let Some(ref v) = path {
            args.insert("path".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        if let Some(ref v) = status_code {
            args.insert("statusCode".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        if let Some(ref v) = endpoint_name {
            args.insert("endpointName".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        let result = self.client.invoke_capability("Aspire.Hosting/withHttpHealthCheck", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResourceWithEndpoints::new(handle, self.client.clone()))
    }

    /// Adds a resource command
    pub fn with_command(&self, name: &str, display_name: &str, execute_command: impl Fn(Vec<Value>) -> Value + Send + Sync + 'static, command_options: Option<CommandOptions>) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("name".to_string(), serde_json::to_value(&name).unwrap_or(Value::Null));
        args.insert("displayName".to_string(), serde_json::to_value(&display_name).unwrap_or(Value::Null));
        let callback_id = register_callback(execute_command);
        args.insert("executeCommand".to_string(), Value::String(callback_id));
        if let Some(ref v) = command_options {
            args.insert("commandOptions".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        let result = self.client.invoke_capability("Aspire.Hosting/withCommand", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Configures developer certificate trust
    pub fn with_developer_certificate_trust(&self, trust: bool) -> Result<IResourceWithEnvironment, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("trust".to_string(), serde_json::to_value(&trust).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting/withDeveloperCertificateTrust", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResourceWithEnvironment::new(handle, self.client.clone()))
    }

    /// Sets the certificate trust scope
    pub fn with_certificate_trust_scope(&self, scope: CertificateTrustScope) -> Result<IResourceWithEnvironment, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("scope".to_string(), serde_json::to_value(&scope).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting/withCertificateTrustScope", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResourceWithEnvironment::new(handle, self.client.clone()))
    }

    /// Configures HTTPS with a developer certificate
    pub fn with_https_developer_certificate(&self, password: Option<&ParameterResource>) -> Result<IResourceWithEnvironment, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        if let Some(ref v) = password {
            args.insert("password".to_string(), v.handle().to_json());
        }
        let result = self.client.invoke_capability("Aspire.Hosting/withParameterHttpsDeveloperCertificate", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResourceWithEnvironment::new(handle, self.client.clone()))
    }

    /// Removes HTTPS certificate configuration
    pub fn without_https_certificate(&self) -> Result<IResourceWithEnvironment, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        let result = self.client.invoke_capability("Aspire.Hosting/withoutHttpsCertificate", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResourceWithEnvironment::new(handle, self.client.clone()))
    }

    /// Sets the parent relationship
    pub fn with_parent_relationship(&self, parent: &IResource) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("parent".to_string(), parent.handle().to_json());
        let result = self.client.invoke_capability("Aspire.Hosting/withBuilderParentRelationship", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Sets a child relationship
    pub fn with_child_relationship(&self, child: &IResource) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("child".to_string(), child.handle().to_json());
        let result = self.client.invoke_capability("Aspire.Hosting/withBuilderChildRelationship", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Sets the icon for the resource
    pub fn with_icon_name(&self, icon_name: &str, icon_variant: Option<IconVariant>) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("iconName".to_string(), serde_json::to_value(&icon_name).unwrap_or(Value::Null));
        if let Some(ref v) = icon_variant {
            args.insert("iconVariant".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        let result = self.client.invoke_capability("Aspire.Hosting/withIconName", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Adds an HTTP health probe to the resource
    pub fn with_http_probe(&self, probe_type: ProbeType, path: Option<&str>, initial_delay_seconds: Option<f64>, period_seconds: Option<f64>, timeout_seconds: Option<f64>, failure_threshold: Option<f64>, success_threshold: Option<f64>, endpoint_name: Option<&str>) -> Result<IResourceWithEndpoints, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("probeType".to_string(), serde_json::to_value(&probe_type).unwrap_or(Value::Null));
        if let Some(ref v) = path {
            args.insert("path".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        if let Some(ref v) = initial_delay_seconds {
            args.insert("initialDelaySeconds".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        if let Some(ref v) = period_seconds {
            args.insert("periodSeconds".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        if let Some(ref v) = timeout_seconds {
            args.insert("timeoutSeconds".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        if let Some(ref v) = failure_threshold {
            args.insert("failureThreshold".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        if let Some(ref v) = success_threshold {
            args.insert("successThreshold".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        if let Some(ref v) = endpoint_name {
            args.insert("endpointName".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        let result = self.client.invoke_capability("Aspire.Hosting/withHttpProbe", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResourceWithEndpoints::new(handle, self.client.clone()))
    }

    /// Excludes the resource from MCP server exposure
    pub fn exclude_from_mcp(&self) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        let result = self.client.invoke_capability("Aspire.Hosting/excludeFromMcp", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Sets the remote image name for publishing
    pub fn with_remote_image_name(&self, remote_image_name: &str) -> Result<IComputeResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("remoteImageName".to_string(), serde_json::to_value(&remote_image_name).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting/withRemoteImageName", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IComputeResource::new(handle, self.client.clone()))
    }

    /// Sets the remote image tag for publishing
    pub fn with_remote_image_tag(&self, remote_image_tag: &str) -> Result<IComputeResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("remoteImageTag".to_string(), serde_json::to_value(&remote_image_tag).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting/withRemoteImageTag", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IComputeResource::new(handle, self.client.clone()))
    }

    /// Adds a pipeline step to the resource
    pub fn with_pipeline_step_factory(&self, step_name: &str, callback: impl Fn(Vec<Value>) -> Value + Send + Sync + 'static, depends_on: Option<Vec<String>>, required_by: Option<Vec<String>>, tags: Option<Vec<String>>, description: Option<&str>) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("stepName".to_string(), serde_json::to_value(&step_name).unwrap_or(Value::Null));
        let callback_id = register_callback(callback);
        args.insert("callback".to_string(), Value::String(callback_id));
        if let Some(ref v) = depends_on {
            args.insert("dependsOn".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        if let Some(ref v) = required_by {
            args.insert("requiredBy".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        if let Some(ref v) = tags {
            args.insert("tags".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        if let Some(ref v) = description {
            args.insert("description".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        let result = self.client.invoke_capability("Aspire.Hosting/withPipelineStepFactory", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Configures pipeline step dependencies via a callback
    pub fn with_pipeline_configuration(&self, callback: impl Fn(Vec<Value>) -> Value + Send + Sync + 'static) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        let callback_id = register_callback(callback);
        args.insert("callback".to_string(), Value::String(callback_id));
        let result = self.client.invoke_capability("Aspire.Hosting/withPipelineConfiguration", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Gets the resource name
    pub fn get_resource_name(&self) -> Result<String, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("resource".to_string(), self.handle.to_json());
        let result = self.client.invoke_capability("Aspire.Hosting/getResourceName", args)?;
        Ok(serde_json::from_value(result)?)
    }

    /// Subscribes to the BeforeResourceStarted event
    pub fn on_before_resource_started(&self, callback: impl Fn(Vec<Value>) -> Value + Send + Sync + 'static) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        let callback_id = register_callback(callback);
        args.insert("callback".to_string(), Value::String(callback_id));
        let result = self.client.invoke_capability("Aspire.Hosting/onBeforeResourceStarted", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Subscribes to the ResourceStopped event
    pub fn on_resource_stopped(&self, callback: impl Fn(Vec<Value>) -> Value + Send + Sync + 'static) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        let callback_id = register_callback(callback);
        args.insert("callback".to_string(), Value::String(callback_id));
        let result = self.client.invoke_capability("Aspire.Hosting/onResourceStopped", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Subscribes to the InitializeResource event
    pub fn on_initialize_resource(&self, callback: impl Fn(Vec<Value>) -> Value + Send + Sync + 'static) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        let callback_id = register_callback(callback);
        args.insert("callback".to_string(), Value::String(callback_id));
        let result = self.client.invoke_capability("Aspire.Hosting/onInitializeResource", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Subscribes to the ResourceEndpointsAllocated event
    pub fn on_resource_endpoints_allocated(&self, callback: impl Fn(Vec<Value>) -> Value + Send + Sync + 'static) -> Result<IResourceWithEndpoints, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        let callback_id = register_callback(callback);
        args.insert("callback".to_string(), Value::String(callback_id));
        let result = self.client.invoke_capability("Aspire.Hosting/onResourceEndpointsAllocated", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResourceWithEndpoints::new(handle, self.client.clone()))
    }

    /// Subscribes to the ResourceReady event
    pub fn on_resource_ready(&self, callback: impl Fn(Vec<Value>) -> Value + Send + Sync + 'static) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        let callback_id = register_callback(callback);
        args.insert("callback".to_string(), Value::String(callback_id));
        let result = self.client.invoke_capability("Aspire.Hosting/onResourceReady", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
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

    /// Adds a dependency from a string or another resource
    pub fn with_union_dependency(&self, dependency: Value) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("dependency".to_string(), serde_json::to_value(&dependency).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting.CodeGeneration.Rust.Tests/withUnionDependency", args)?;
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

    /// Adds a label to the resource
    pub fn with_merge_label(&self, label: &str) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("label".to_string(), serde_json::to_value(&label).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting.CodeGeneration.Rust.Tests/withMergeLabel", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Adds a categorized label to the resource
    pub fn with_merge_label_categorized(&self, label: &str, category: &str) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("label".to_string(), serde_json::to_value(&label).unwrap_or(Value::Null));
        args.insert("category".to_string(), serde_json::to_value(&category).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting.CodeGeneration.Rust.Tests/withMergeLabelCategorized", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Configures a named endpoint
    pub fn with_merge_endpoint(&self, endpoint_name: &str, port: f64) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("endpointName".to_string(), serde_json::to_value(&endpoint_name).unwrap_or(Value::Null));
        args.insert("port".to_string(), serde_json::to_value(&port).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting.CodeGeneration.Rust.Tests/withMergeEndpoint", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Configures a named endpoint with scheme
    pub fn with_merge_endpoint_scheme(&self, endpoint_name: &str, port: f64, scheme: &str) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("endpointName".to_string(), serde_json::to_value(&endpoint_name).unwrap_or(Value::Null));
        args.insert("port".to_string(), serde_json::to_value(&port).unwrap_or(Value::Null));
        args.insert("scheme".to_string(), serde_json::to_value(&scheme).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting.CodeGeneration.Rust.Tests/withMergeEndpointScheme", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Configures resource logging
    pub fn with_merge_logging(&self, log_level: &str, enable_console: Option<bool>, max_files: Option<f64>) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("logLevel".to_string(), serde_json::to_value(&log_level).unwrap_or(Value::Null));
        if let Some(ref v) = enable_console {
            args.insert("enableConsole".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        if let Some(ref v) = max_files {
            args.insert("maxFiles".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        let result = self.client.invoke_capability("Aspire.Hosting.CodeGeneration.Rust.Tests/withMergeLogging", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Configures resource logging with file path
    pub fn with_merge_logging_path(&self, log_level: &str, log_path: &str, enable_console: Option<bool>, max_files: Option<f64>) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("logLevel".to_string(), serde_json::to_value(&log_level).unwrap_or(Value::Null));
        args.insert("logPath".to_string(), serde_json::to_value(&log_path).unwrap_or(Value::Null));
        if let Some(ref v) = enable_console {
            args.insert("enableConsole".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        if let Some(ref v) = max_files {
            args.insert("maxFiles".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        let result = self.client.invoke_capability("Aspire.Hosting.CodeGeneration.Rust.Tests/withMergeLoggingPath", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Configures a route
    pub fn with_merge_route(&self, path: &str, method: &str, handler: &str, priority: f64) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("path".to_string(), serde_json::to_value(&path).unwrap_or(Value::Null));
        args.insert("method".to_string(), serde_json::to_value(&method).unwrap_or(Value::Null));
        args.insert("handler".to_string(), serde_json::to_value(&handler).unwrap_or(Value::Null));
        args.insert("priority".to_string(), serde_json::to_value(&priority).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting.CodeGeneration.Rust.Tests/withMergeRoute", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Configures a route with middleware
    pub fn with_merge_route_middleware(&self, path: &str, method: &str, handler: &str, priority: f64, middleware: &str) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("path".to_string(), serde_json::to_value(&path).unwrap_or(Value::Null));
        args.insert("method".to_string(), serde_json::to_value(&method).unwrap_or(Value::Null));
        args.insert("handler".to_string(), serde_json::to_value(&handler).unwrap_or(Value::Null));
        args.insert("priority".to_string(), serde_json::to_value(&priority).unwrap_or(Value::Null));
        args.insert("middleware".to_string(), serde_json::to_value(&middleware).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting.CodeGeneration.Rust.Tests/withMergeRouteMiddleware", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }
}

/// Wrapper for Aspire.Hosting/Aspire.Hosting.ApplicationModel.EndpointReference
pub struct EndpointReference {
    handle: Handle,
    client: Arc<AspireClient>,
}

impl HasHandle for EndpointReference {
    fn handle(&self) -> &Handle {
        &self.handle
    }
}

impl EndpointReference {
    pub fn new(handle: Handle, client: Arc<AspireClient>) -> Self {
        Self { handle, client }
    }

    pub fn handle(&self) -> &Handle {
        &self.handle
    }

    pub fn client(&self) -> &Arc<AspireClient> {
        &self.client
    }

    /// Gets the Resource property
    pub fn resource(&self) -> Result<IResourceWithEndpoints, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("context".to_string(), self.handle.to_json());
        let result = self.client.invoke_capability("Aspire.Hosting.ApplicationModel/EndpointReference.resource", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResourceWithEndpoints::new(handle, self.client.clone()))
    }

    /// Gets the EndpointName property
    pub fn endpoint_name(&self) -> Result<String, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("context".to_string(), self.handle.to_json());
        let result = self.client.invoke_capability("Aspire.Hosting.ApplicationModel/EndpointReference.endpointName", args)?;
        Ok(serde_json::from_value(result)?)
    }

    /// Gets the ErrorMessage property
    pub fn error_message(&self) -> Result<String, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("context".to_string(), self.handle.to_json());
        let result = self.client.invoke_capability("Aspire.Hosting.ApplicationModel/EndpointReference.errorMessage", args)?;
        Ok(serde_json::from_value(result)?)
    }

    /// Sets the ErrorMessage property
    pub fn set_error_message(&self, value: &str) -> Result<EndpointReference, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("context".to_string(), self.handle.to_json());
        args.insert("value".to_string(), serde_json::to_value(&value).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting.ApplicationModel/EndpointReference.setErrorMessage", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(EndpointReference::new(handle, self.client.clone()))
    }

    /// Gets the IsAllocated property
    pub fn is_allocated(&self) -> Result<bool, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("context".to_string(), self.handle.to_json());
        let result = self.client.invoke_capability("Aspire.Hosting.ApplicationModel/EndpointReference.isAllocated", args)?;
        Ok(serde_json::from_value(result)?)
    }

    /// Gets the Exists property
    pub fn exists(&self) -> Result<bool, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("context".to_string(), self.handle.to_json());
        let result = self.client.invoke_capability("Aspire.Hosting.ApplicationModel/EndpointReference.exists", args)?;
        Ok(serde_json::from_value(result)?)
    }

    /// Gets the IsHttp property
    pub fn is_http(&self) -> Result<bool, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("context".to_string(), self.handle.to_json());
        let result = self.client.invoke_capability("Aspire.Hosting.ApplicationModel/EndpointReference.isHttp", args)?;
        Ok(serde_json::from_value(result)?)
    }

    /// Gets the IsHttps property
    pub fn is_https(&self) -> Result<bool, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("context".to_string(), self.handle.to_json());
        let result = self.client.invoke_capability("Aspire.Hosting.ApplicationModel/EndpointReference.isHttps", args)?;
        Ok(serde_json::from_value(result)?)
    }

    /// Gets the TlsEnabled property
    pub fn tls_enabled(&self) -> Result<bool, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("context".to_string(), self.handle.to_json());
        let result = self.client.invoke_capability("Aspire.Hosting.ApplicationModel/EndpointReference.tlsEnabled", args)?;
        Ok(serde_json::from_value(result)?)
    }

    /// Gets the ExcludeReferenceEndpoint property
    pub fn exclude_reference_endpoint(&self) -> Result<bool, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("context".to_string(), self.handle.to_json());
        let result = self.client.invoke_capability("Aspire.Hosting.ApplicationModel/EndpointReference.excludeReferenceEndpoint", args)?;
        Ok(serde_json::from_value(result)?)
    }

    /// Gets the Port property
    pub fn port(&self) -> Result<f64, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("context".to_string(), self.handle.to_json());
        let result = self.client.invoke_capability("Aspire.Hosting.ApplicationModel/EndpointReference.port", args)?;
        Ok(serde_json::from_value(result)?)
    }

    /// Gets the TargetPort property
    pub fn target_port(&self) -> Result<f64, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("context".to_string(), self.handle.to_json());
        let result = self.client.invoke_capability("Aspire.Hosting.ApplicationModel/EndpointReference.targetPort", args)?;
        Ok(serde_json::from_value(result)?)
    }

    /// Gets the Host property
    pub fn host(&self) -> Result<String, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("context".to_string(), self.handle.to_json());
        let result = self.client.invoke_capability("Aspire.Hosting.ApplicationModel/EndpointReference.host", args)?;
        Ok(serde_json::from_value(result)?)
    }

    /// Gets the Scheme property
    pub fn scheme(&self) -> Result<String, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("context".to_string(), self.handle.to_json());
        let result = self.client.invoke_capability("Aspire.Hosting.ApplicationModel/EndpointReference.scheme", args)?;
        Ok(serde_json::from_value(result)?)
    }

    /// Gets the Url property
    pub fn url(&self) -> Result<String, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("context".to_string(), self.handle.to_json());
        let result = self.client.invoke_capability("Aspire.Hosting.ApplicationModel/EndpointReference.url", args)?;
        Ok(serde_json::from_value(result)?)
    }

    /// Gets the URL of the endpoint asynchronously
    pub fn get_value_async(&self, cancellation_token: Option<&CancellationToken>) -> Result<String, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("context".to_string(), self.handle.to_json());
        if let Some(token) = cancellation_token {
            let token_id = register_cancellation(token, self.client.clone());
            args.insert("cancellationToken".to_string(), Value::String(token_id));
        }
        let result = self.client.invoke_capability("Aspire.Hosting.ApplicationModel/getValueAsync", args)?;
        Ok(serde_json::from_value(result)?)
    }

    /// Gets a conditional expression that resolves to the enabledValue when TLS is enabled on the endpoint, or to the disabledValue otherwise.
    pub fn get_tls_value(&self, enabled_value: ReferenceExpression, disabled_value: ReferenceExpression) -> Result<ReferenceExpression, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("context".to_string(), self.handle.to_json());
        args.insert("enabledValue".to_string(), serde_json::to_value(&enabled_value).unwrap_or(Value::Null));
        args.insert("disabledValue".to_string(), serde_json::to_value(&disabled_value).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting.ApplicationModel/EndpointReference.getTlsValue", args)?;
        Ok(serde_json::from_value(result)?)
    }
}

/// Wrapper for Aspire.Hosting/Aspire.Hosting.ApplicationModel.EndpointReferenceExpression
pub struct EndpointReferenceExpression {
    handle: Handle,
    client: Arc<AspireClient>,
}

impl HasHandle for EndpointReferenceExpression {
    fn handle(&self) -> &Handle {
        &self.handle
    }
}

impl EndpointReferenceExpression {
    pub fn new(handle: Handle, client: Arc<AspireClient>) -> Self {
        Self { handle, client }
    }

    pub fn handle(&self) -> &Handle {
        &self.handle
    }

    pub fn client(&self) -> &Arc<AspireClient> {
        &self.client
    }

    /// Gets the Endpoint property
    pub fn endpoint(&self) -> Result<EndpointReference, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("context".to_string(), self.handle.to_json());
        let result = self.client.invoke_capability("Aspire.Hosting.ApplicationModel/EndpointReferenceExpression.endpoint", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(EndpointReference::new(handle, self.client.clone()))
    }

    /// Gets the Property property
    pub fn property(&self) -> Result<EndpointProperty, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("context".to_string(), self.handle.to_json());
        let result = self.client.invoke_capability("Aspire.Hosting.ApplicationModel/EndpointReferenceExpression.property", args)?;
        Ok(serde_json::from_value(result)?)
    }

    /// Gets the ValueExpression property
    pub fn value_expression(&self) -> Result<String, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("context".to_string(), self.handle.to_json());
        let result = self.client.invoke_capability("Aspire.Hosting.ApplicationModel/EndpointReferenceExpression.valueExpression", args)?;
        Ok(serde_json::from_value(result)?)
    }
}

/// Wrapper for Aspire.Hosting/Aspire.Hosting.ApplicationModel.EnvironmentCallbackContext
pub struct EnvironmentCallbackContext {
    handle: Handle,
    client: Arc<AspireClient>,
}

impl HasHandle for EnvironmentCallbackContext {
    fn handle(&self) -> &Handle {
        &self.handle
    }
}

impl EnvironmentCallbackContext {
    pub fn new(handle: Handle, client: Arc<AspireClient>) -> Self {
        Self { handle, client }
    }

    pub fn handle(&self) -> &Handle {
        &self.handle
    }

    pub fn client(&self) -> &Arc<AspireClient> {
        &self.client
    }

    /// Gets the EnvironmentVariables property
    pub fn environment_variables(&self) -> AspireDict<String, Value> {
        AspireDict::with_getter(self.handle.clone(), self.client.clone(), "Aspire.Hosting.ApplicationModel/EnvironmentCallbackContext.environmentVariables")
    }

    /// Gets the CancellationToken property
    pub fn cancellation_token(&self) -> Result<CancellationToken, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("context".to_string(), self.handle.to_json());
        let result = self.client.invoke_capability("Aspire.Hosting.ApplicationModel/EnvironmentCallbackContext.cancellationToken", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(CancellationToken::new(handle, self.client.clone()))
    }

    /// Gets the Logger property
    pub fn logger(&self) -> Result<ILogger, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("context".to_string(), self.handle.to_json());
        let result = self.client.invoke_capability("Aspire.Hosting.ApplicationModel/EnvironmentCallbackContext.logger", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(ILogger::new(handle, self.client.clone()))
    }

    /// Sets the Logger property
    pub fn set_logger(&self, value: &ILogger) -> Result<EnvironmentCallbackContext, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("context".to_string(), self.handle.to_json());
        args.insert("value".to_string(), value.handle().to_json());
        let result = self.client.invoke_capability("Aspire.Hosting.ApplicationModel/EnvironmentCallbackContext.setLogger", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(EnvironmentCallbackContext::new(handle, self.client.clone()))
    }

    /// Gets the Resource property
    pub fn resource(&self) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("context".to_string(), self.handle.to_json());
        let result = self.client.invoke_capability("Aspire.Hosting.ApplicationModel/EnvironmentCallbackContext.resource", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Gets the ExecutionContext property
    pub fn execution_context(&self) -> Result<DistributedApplicationExecutionContext, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("context".to_string(), self.handle.to_json());
        let result = self.client.invoke_capability("Aspire.Hosting.ApplicationModel/EnvironmentCallbackContext.executionContext", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(DistributedApplicationExecutionContext::new(handle, self.client.clone()))
    }
}

/// Wrapper for Aspire.Hosting/Aspire.Hosting.ApplicationModel.ExecutableResource
pub struct ExecutableResource {
    handle: Handle,
    client: Arc<AspireClient>,
}

impl HasHandle for ExecutableResource {
    fn handle(&self) -> &Handle {
        &self.handle
    }
}

impl ExecutableResource {
    pub fn new(handle: Handle, client: Arc<AspireClient>) -> Self {
        Self { handle, client }
    }

    pub fn handle(&self) -> &Handle {
        &self.handle
    }

    pub fn client(&self) -> &Arc<AspireClient> {
        &self.client
    }

    /// Configures a resource to use a container registry
    pub fn with_container_registry(&self, registry: &IResource) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("registry".to_string(), registry.handle().to_json());
        let result = self.client.invoke_capability("Aspire.Hosting/withContainerRegistry", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Sets the base image for a Dockerfile build
    pub fn with_dockerfile_base_image(&self, build_image: Option<&str>, runtime_image: Option<&str>) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        if let Some(ref v) = build_image {
            args.insert("buildImage".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        if let Some(ref v) = runtime_image {
            args.insert("runtimeImage".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        let result = self.client.invoke_capability("Aspire.Hosting/withDockerfileBaseImage", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Publishes the executable as a Docker container
    pub fn publish_as_docker_file(&self) -> Result<ExecutableResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        let result = self.client.invoke_capability("Aspire.Hosting/publishAsDockerFile", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(ExecutableResource::new(handle, self.client.clone()))
    }

    /// Publishes an executable as a Docker file with optional container configuration
    pub fn publish_as_docker_file_with_configure(&self, configure: impl Fn(Vec<Value>) -> Value + Send + Sync + 'static) -> Result<ExecutableResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        let callback_id = register_callback(configure);
        args.insert("configure".to_string(), Value::String(callback_id));
        let result = self.client.invoke_capability("Aspire.Hosting/publishAsDockerFileWithConfigure", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(ExecutableResource::new(handle, self.client.clone()))
    }

    /// Sets the executable command
    pub fn with_executable_command(&self, command: &str) -> Result<ExecutableResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("command".to_string(), serde_json::to_value(&command).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting/withExecutableCommand", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(ExecutableResource::new(handle, self.client.clone()))
    }

    /// Sets the executable working directory
    pub fn with_working_directory(&self, working_directory: &str) -> Result<ExecutableResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("workingDirectory".to_string(), serde_json::to_value(&working_directory).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting/withWorkingDirectory", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(ExecutableResource::new(handle, self.client.clone()))
    }

    /// Configures an MCP server endpoint on the resource
    pub fn with_mcp_server(&self, path: Option<&str>, endpoint_name: Option<&str>) -> Result<IResourceWithEndpoints, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        if let Some(ref v) = path {
            args.insert("path".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        if let Some(ref v) = endpoint_name {
            args.insert("endpointName".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        let result = self.client.invoke_capability("Aspire.Hosting/withMcpServer", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResourceWithEndpoints::new(handle, self.client.clone()))
    }

    /// Configures OTLP telemetry export
    pub fn with_otlp_exporter(&self) -> Result<IResourceWithEnvironment, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        let result = self.client.invoke_capability("Aspire.Hosting/withOtlpExporter", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResourceWithEnvironment::new(handle, self.client.clone()))
    }

    /// Configures OTLP telemetry export with specific protocol
    pub fn with_otlp_exporter_protocol(&self, protocol: OtlpProtocol) -> Result<IResourceWithEnvironment, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("protocol".to_string(), serde_json::to_value(&protocol).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting/withOtlpExporterProtocol", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResourceWithEnvironment::new(handle, self.client.clone()))
    }

    /// Adds a required command dependency
    pub fn with_required_command(&self, command: &str, help_link: Option<&str>) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("command".to_string(), serde_json::to_value(&command).unwrap_or(Value::Null));
        if let Some(ref v) = help_link {
            args.insert("helpLink".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        let result = self.client.invoke_capability("Aspire.Hosting/withRequiredCommand", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Sets an environment variable
    pub fn with_environment(&self, name: &str, value: Value) -> Result<IResourceWithEnvironment, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("name".to_string(), serde_json::to_value(&name).unwrap_or(Value::Null));
        args.insert("value".to_string(), serde_json::to_value(&value).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting/withEnvironment", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResourceWithEnvironment::new(handle, self.client.clone()))
    }

    /// Sets an environment variable from a reference expression
    pub fn with_environment_expression(&self, name: &str, value: ReferenceExpression) -> Result<IResourceWithEnvironment, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("name".to_string(), serde_json::to_value(&name).unwrap_or(Value::Null));
        args.insert("value".to_string(), serde_json::to_value(&value).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting/withEnvironmentExpression", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResourceWithEnvironment::new(handle, self.client.clone()))
    }

    /// Sets environment variables via callback
    pub fn with_environment_callback(&self, callback: impl Fn(Vec<Value>) -> Value + Send + Sync + 'static) -> Result<IResourceWithEnvironment, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        let callback_id = register_callback(callback);
        args.insert("callback".to_string(), Value::String(callback_id));
        let result = self.client.invoke_capability("Aspire.Hosting/withEnvironmentCallback", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResourceWithEnvironment::new(handle, self.client.clone()))
    }

    /// Sets an environment variable from an endpoint reference
    pub fn with_environment_endpoint(&self, name: &str, endpoint_reference: &EndpointReference) -> Result<IResourceWithEnvironment, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("name".to_string(), serde_json::to_value(&name).unwrap_or(Value::Null));
        args.insert("endpointReference".to_string(), endpoint_reference.handle().to_json());
        let result = self.client.invoke_capability("Aspire.Hosting/withEnvironmentEndpoint", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResourceWithEnvironment::new(handle, self.client.clone()))
    }

    /// Sets an environment variable from a parameter resource
    pub fn with_environment_parameter(&self, name: &str, parameter: &ParameterResource) -> Result<IResourceWithEnvironment, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("name".to_string(), serde_json::to_value(&name).unwrap_or(Value::Null));
        args.insert("parameter".to_string(), parameter.handle().to_json());
        let result = self.client.invoke_capability("Aspire.Hosting/withEnvironmentParameter", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResourceWithEnvironment::new(handle, self.client.clone()))
    }

    /// Sets an environment variable from a connection string resource
    pub fn with_environment_connection_string(&self, env_var_name: &str, resource: &IResourceWithConnectionString) -> Result<IResourceWithEnvironment, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("envVarName".to_string(), serde_json::to_value(&env_var_name).unwrap_or(Value::Null));
        args.insert("resource".to_string(), resource.handle().to_json());
        let result = self.client.invoke_capability("Aspire.Hosting/withEnvironmentConnectionString", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResourceWithEnvironment::new(handle, self.client.clone()))
    }

    /// Adds arguments
    pub fn with_args(&self, args: Vec<String>) -> Result<IResourceWithArgs, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("args".to_string(), serde_json::to_value(&args).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting/withArgs", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResourceWithArgs::new(handle, self.client.clone()))
    }

    /// Sets command-line arguments via callback
    pub fn with_args_callback(&self, callback: impl Fn(Vec<Value>) -> Value + Send + Sync + 'static) -> Result<IResourceWithArgs, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        let callback_id = register_callback(callback);
        args.insert("callback".to_string(), Value::String(callback_id));
        let result = self.client.invoke_capability("Aspire.Hosting/withArgsCallback", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResourceWithArgs::new(handle, self.client.clone()))
    }

    /// Adds a reference to another resource
    pub fn with_reference(&self, source: &IResource, connection_name: Option<&str>, optional: Option<bool>, name: Option<&str>) -> Result<IResourceWithEnvironment, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("source".to_string(), source.handle().to_json());
        if let Some(ref v) = connection_name {
            args.insert("connectionName".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        if let Some(ref v) = optional {
            args.insert("optional".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        if let Some(ref v) = name {
            args.insert("name".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        let result = self.client.invoke_capability("Aspire.Hosting/withReference", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResourceWithEnvironment::new(handle, self.client.clone()))
    }

    /// Adds a reference to a URI
    pub fn with_reference_uri(&self, name: &str, uri: &str) -> Result<IResourceWithEnvironment, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("name".to_string(), serde_json::to_value(&name).unwrap_or(Value::Null));
        args.insert("uri".to_string(), serde_json::to_value(&uri).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting/withReferenceUri", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResourceWithEnvironment::new(handle, self.client.clone()))
    }

    /// Adds a reference to an external service
    pub fn with_reference_external_service(&self, external_service: &ExternalServiceResource) -> Result<IResourceWithEnvironment, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("externalService".to_string(), external_service.handle().to_json());
        let result = self.client.invoke_capability("Aspire.Hosting/withReferenceExternalService", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResourceWithEnvironment::new(handle, self.client.clone()))
    }

    /// Adds a reference to an endpoint
    pub fn with_reference_endpoint(&self, endpoint_reference: &EndpointReference) -> Result<IResourceWithEnvironment, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("endpointReference".to_string(), endpoint_reference.handle().to_json());
        let result = self.client.invoke_capability("Aspire.Hosting/withReferenceEndpoint", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResourceWithEnvironment::new(handle, self.client.clone()))
    }

    /// Adds a network endpoint
    pub fn with_endpoint(&self, port: Option<f64>, target_port: Option<f64>, scheme: Option<&str>, name: Option<&str>, env: Option<&str>, is_proxied: Option<bool>, is_external: Option<bool>, protocol: Option<ProtocolType>) -> Result<IResourceWithEndpoints, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        if let Some(ref v) = port {
            args.insert("port".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        if let Some(ref v) = target_port {
            args.insert("targetPort".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        if let Some(ref v) = scheme {
            args.insert("scheme".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        if let Some(ref v) = name {
            args.insert("name".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        if let Some(ref v) = env {
            args.insert("env".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        if let Some(ref v) = is_proxied {
            args.insert("isProxied".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        if let Some(ref v) = is_external {
            args.insert("isExternal".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        if let Some(ref v) = protocol {
            args.insert("protocol".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        let result = self.client.invoke_capability("Aspire.Hosting/withEndpoint", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResourceWithEndpoints::new(handle, self.client.clone()))
    }

    /// Adds an HTTP endpoint
    pub fn with_http_endpoint(&self, port: Option<f64>, target_port: Option<f64>, name: Option<&str>, env: Option<&str>, is_proxied: Option<bool>) -> Result<IResourceWithEndpoints, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        if let Some(ref v) = port {
            args.insert("port".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        if let Some(ref v) = target_port {
            args.insert("targetPort".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        if let Some(ref v) = name {
            args.insert("name".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        if let Some(ref v) = env {
            args.insert("env".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        if let Some(ref v) = is_proxied {
            args.insert("isProxied".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        let result = self.client.invoke_capability("Aspire.Hosting/withHttpEndpoint", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResourceWithEndpoints::new(handle, self.client.clone()))
    }

    /// Adds an HTTPS endpoint
    pub fn with_https_endpoint(&self, port: Option<f64>, target_port: Option<f64>, name: Option<&str>, env: Option<&str>, is_proxied: Option<bool>) -> Result<IResourceWithEndpoints, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        if let Some(ref v) = port {
            args.insert("port".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        if let Some(ref v) = target_port {
            args.insert("targetPort".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        if let Some(ref v) = name {
            args.insert("name".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        if let Some(ref v) = env {
            args.insert("env".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        if let Some(ref v) = is_proxied {
            args.insert("isProxied".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        let result = self.client.invoke_capability("Aspire.Hosting/withHttpsEndpoint", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResourceWithEndpoints::new(handle, self.client.clone()))
    }

    /// Makes HTTP endpoints externally accessible
    pub fn with_external_http_endpoints(&self) -> Result<IResourceWithEndpoints, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        let result = self.client.invoke_capability("Aspire.Hosting/withExternalHttpEndpoints", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResourceWithEndpoints::new(handle, self.client.clone()))
    }

    /// Gets an endpoint reference
    pub fn get_endpoint(&self, name: &str) -> Result<EndpointReference, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("name".to_string(), serde_json::to_value(&name).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting/getEndpoint", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(EndpointReference::new(handle, self.client.clone()))
    }

    /// Configures resource for HTTP/2
    pub fn as_http2_service(&self) -> Result<IResourceWithEndpoints, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        let result = self.client.invoke_capability("Aspire.Hosting/asHttp2Service", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResourceWithEndpoints::new(handle, self.client.clone()))
    }

    /// Customizes displayed URLs via callback
    pub fn with_urls_callback(&self, callback: impl Fn(Vec<Value>) -> Value + Send + Sync + 'static) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        let callback_id = register_callback(callback);
        args.insert("callback".to_string(), Value::String(callback_id));
        let result = self.client.invoke_capability("Aspire.Hosting/withUrlsCallback", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Adds or modifies displayed URLs
    pub fn with_url(&self, url: &str, display_text: Option<&str>) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("url".to_string(), serde_json::to_value(&url).unwrap_or(Value::Null));
        if let Some(ref v) = display_text {
            args.insert("displayText".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        let result = self.client.invoke_capability("Aspire.Hosting/withUrl", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Adds a URL using a reference expression
    pub fn with_url_expression(&self, url: ReferenceExpression, display_text: Option<&str>) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("url".to_string(), serde_json::to_value(&url).unwrap_or(Value::Null));
        if let Some(ref v) = display_text {
            args.insert("displayText".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        let result = self.client.invoke_capability("Aspire.Hosting/withUrlExpression", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Customizes the URL for a specific endpoint via callback
    pub fn with_url_for_endpoint(&self, endpoint_name: &str, callback: impl Fn(Vec<Value>) -> Value + Send + Sync + 'static) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("endpointName".to_string(), serde_json::to_value(&endpoint_name).unwrap_or(Value::Null));
        let callback_id = register_callback(callback);
        args.insert("callback".to_string(), Value::String(callback_id));
        let result = self.client.invoke_capability("Aspire.Hosting/withUrlForEndpoint", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Adds a URL for a specific endpoint via factory callback
    pub fn with_url_for_endpoint_factory(&self, endpoint_name: &str, callback: impl Fn(Vec<Value>) -> Value + Send + Sync + 'static) -> Result<IResourceWithEndpoints, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("endpointName".to_string(), serde_json::to_value(&endpoint_name).unwrap_or(Value::Null));
        let callback_id = register_callback(callback);
        args.insert("callback".to_string(), Value::String(callback_id));
        let result = self.client.invoke_capability("Aspire.Hosting/withUrlForEndpointFactory", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResourceWithEndpoints::new(handle, self.client.clone()))
    }

    /// Excludes the resource from the deployment manifest
    pub fn exclude_from_manifest(&self) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        let result = self.client.invoke_capability("Aspire.Hosting/excludeFromManifest", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Waits for another resource to be ready
    pub fn wait_for(&self, dependency: &IResource) -> Result<IResourceWithWaitSupport, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("dependency".to_string(), dependency.handle().to_json());
        let result = self.client.invoke_capability("Aspire.Hosting/waitForResource", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResourceWithWaitSupport::new(handle, self.client.clone()))
    }

    /// Waits for another resource with specific behavior
    pub fn wait_for_with_behavior(&self, dependency: &IResource, wait_behavior: WaitBehavior) -> Result<IResourceWithWaitSupport, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("dependency".to_string(), dependency.handle().to_json());
        args.insert("waitBehavior".to_string(), serde_json::to_value(&wait_behavior).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting/waitForWithBehavior", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResourceWithWaitSupport::new(handle, self.client.clone()))
    }

    /// Waits for another resource to start
    pub fn wait_for_start(&self, dependency: &IResource) -> Result<IResourceWithWaitSupport, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("dependency".to_string(), dependency.handle().to_json());
        let result = self.client.invoke_capability("Aspire.Hosting/waitForResourceStart", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResourceWithWaitSupport::new(handle, self.client.clone()))
    }

    /// Waits for another resource to start with specific behavior
    pub fn wait_for_start_with_behavior(&self, dependency: &IResource, wait_behavior: WaitBehavior) -> Result<IResourceWithWaitSupport, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("dependency".to_string(), dependency.handle().to_json());
        args.insert("waitBehavior".to_string(), serde_json::to_value(&wait_behavior).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting/waitForStartWithBehavior", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResourceWithWaitSupport::new(handle, self.client.clone()))
    }

    /// Prevents resource from starting automatically
    pub fn with_explicit_start(&self) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        let result = self.client.invoke_capability("Aspire.Hosting/withExplicitStart", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Waits for resource completion
    pub fn wait_for_completion(&self, dependency: &IResource, exit_code: Option<f64>) -> Result<IResourceWithWaitSupport, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("dependency".to_string(), dependency.handle().to_json());
        if let Some(ref v) = exit_code {
            args.insert("exitCode".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        let result = self.client.invoke_capability("Aspire.Hosting/waitForResourceCompletion", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResourceWithWaitSupport::new(handle, self.client.clone()))
    }

    /// Adds a health check by key
    pub fn with_health_check(&self, key: &str) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("key".to_string(), serde_json::to_value(&key).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting/withHealthCheck", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Adds an HTTP health check
    pub fn with_http_health_check(&self, path: Option<&str>, status_code: Option<f64>, endpoint_name: Option<&str>) -> Result<IResourceWithEndpoints, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        if let Some(ref v) = path {
            args.insert("path".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        if let Some(ref v) = status_code {
            args.insert("statusCode".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        if let Some(ref v) = endpoint_name {
            args.insert("endpointName".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        let result = self.client.invoke_capability("Aspire.Hosting/withHttpHealthCheck", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResourceWithEndpoints::new(handle, self.client.clone()))
    }

    /// Adds a resource command
    pub fn with_command(&self, name: &str, display_name: &str, execute_command: impl Fn(Vec<Value>) -> Value + Send + Sync + 'static, command_options: Option<CommandOptions>) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("name".to_string(), serde_json::to_value(&name).unwrap_or(Value::Null));
        args.insert("displayName".to_string(), serde_json::to_value(&display_name).unwrap_or(Value::Null));
        let callback_id = register_callback(execute_command);
        args.insert("executeCommand".to_string(), Value::String(callback_id));
        if let Some(ref v) = command_options {
            args.insert("commandOptions".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        let result = self.client.invoke_capability("Aspire.Hosting/withCommand", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Configures developer certificate trust
    pub fn with_developer_certificate_trust(&self, trust: bool) -> Result<IResourceWithEnvironment, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("trust".to_string(), serde_json::to_value(&trust).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting/withDeveloperCertificateTrust", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResourceWithEnvironment::new(handle, self.client.clone()))
    }

    /// Sets the certificate trust scope
    pub fn with_certificate_trust_scope(&self, scope: CertificateTrustScope) -> Result<IResourceWithEnvironment, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("scope".to_string(), serde_json::to_value(&scope).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting/withCertificateTrustScope", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResourceWithEnvironment::new(handle, self.client.clone()))
    }

    /// Configures HTTPS with a developer certificate
    pub fn with_https_developer_certificate(&self, password: Option<&ParameterResource>) -> Result<IResourceWithEnvironment, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        if let Some(ref v) = password {
            args.insert("password".to_string(), v.handle().to_json());
        }
        let result = self.client.invoke_capability("Aspire.Hosting/withParameterHttpsDeveloperCertificate", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResourceWithEnvironment::new(handle, self.client.clone()))
    }

    /// Removes HTTPS certificate configuration
    pub fn without_https_certificate(&self) -> Result<IResourceWithEnvironment, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        let result = self.client.invoke_capability("Aspire.Hosting/withoutHttpsCertificate", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResourceWithEnvironment::new(handle, self.client.clone()))
    }

    /// Sets the parent relationship
    pub fn with_parent_relationship(&self, parent: &IResource) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("parent".to_string(), parent.handle().to_json());
        let result = self.client.invoke_capability("Aspire.Hosting/withBuilderParentRelationship", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Sets a child relationship
    pub fn with_child_relationship(&self, child: &IResource) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("child".to_string(), child.handle().to_json());
        let result = self.client.invoke_capability("Aspire.Hosting/withBuilderChildRelationship", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Sets the icon for the resource
    pub fn with_icon_name(&self, icon_name: &str, icon_variant: Option<IconVariant>) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("iconName".to_string(), serde_json::to_value(&icon_name).unwrap_or(Value::Null));
        if let Some(ref v) = icon_variant {
            args.insert("iconVariant".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        let result = self.client.invoke_capability("Aspire.Hosting/withIconName", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Adds an HTTP health probe to the resource
    pub fn with_http_probe(&self, probe_type: ProbeType, path: Option<&str>, initial_delay_seconds: Option<f64>, period_seconds: Option<f64>, timeout_seconds: Option<f64>, failure_threshold: Option<f64>, success_threshold: Option<f64>, endpoint_name: Option<&str>) -> Result<IResourceWithEndpoints, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("probeType".to_string(), serde_json::to_value(&probe_type).unwrap_or(Value::Null));
        if let Some(ref v) = path {
            args.insert("path".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        if let Some(ref v) = initial_delay_seconds {
            args.insert("initialDelaySeconds".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        if let Some(ref v) = period_seconds {
            args.insert("periodSeconds".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        if let Some(ref v) = timeout_seconds {
            args.insert("timeoutSeconds".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        if let Some(ref v) = failure_threshold {
            args.insert("failureThreshold".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        if let Some(ref v) = success_threshold {
            args.insert("successThreshold".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        if let Some(ref v) = endpoint_name {
            args.insert("endpointName".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        let result = self.client.invoke_capability("Aspire.Hosting/withHttpProbe", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResourceWithEndpoints::new(handle, self.client.clone()))
    }

    /// Excludes the resource from MCP server exposure
    pub fn exclude_from_mcp(&self) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        let result = self.client.invoke_capability("Aspire.Hosting/excludeFromMcp", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Sets the remote image name for publishing
    pub fn with_remote_image_name(&self, remote_image_name: &str) -> Result<IComputeResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("remoteImageName".to_string(), serde_json::to_value(&remote_image_name).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting/withRemoteImageName", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IComputeResource::new(handle, self.client.clone()))
    }

    /// Sets the remote image tag for publishing
    pub fn with_remote_image_tag(&self, remote_image_tag: &str) -> Result<IComputeResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("remoteImageTag".to_string(), serde_json::to_value(&remote_image_tag).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting/withRemoteImageTag", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IComputeResource::new(handle, self.client.clone()))
    }

    /// Adds a pipeline step to the resource
    pub fn with_pipeline_step_factory(&self, step_name: &str, callback: impl Fn(Vec<Value>) -> Value + Send + Sync + 'static, depends_on: Option<Vec<String>>, required_by: Option<Vec<String>>, tags: Option<Vec<String>>, description: Option<&str>) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("stepName".to_string(), serde_json::to_value(&step_name).unwrap_or(Value::Null));
        let callback_id = register_callback(callback);
        args.insert("callback".to_string(), Value::String(callback_id));
        if let Some(ref v) = depends_on {
            args.insert("dependsOn".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        if let Some(ref v) = required_by {
            args.insert("requiredBy".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        if let Some(ref v) = tags {
            args.insert("tags".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        if let Some(ref v) = description {
            args.insert("description".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        let result = self.client.invoke_capability("Aspire.Hosting/withPipelineStepFactory", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Configures pipeline step dependencies via a callback
    pub fn with_pipeline_configuration(&self, callback: impl Fn(Vec<Value>) -> Value + Send + Sync + 'static) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        let callback_id = register_callback(callback);
        args.insert("callback".to_string(), Value::String(callback_id));
        let result = self.client.invoke_capability("Aspire.Hosting/withPipelineConfiguration", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Gets the resource name
    pub fn get_resource_name(&self) -> Result<String, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("resource".to_string(), self.handle.to_json());
        let result = self.client.invoke_capability("Aspire.Hosting/getResourceName", args)?;
        Ok(serde_json::from_value(result)?)
    }

    /// Subscribes to the BeforeResourceStarted event
    pub fn on_before_resource_started(&self, callback: impl Fn(Vec<Value>) -> Value + Send + Sync + 'static) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        let callback_id = register_callback(callback);
        args.insert("callback".to_string(), Value::String(callback_id));
        let result = self.client.invoke_capability("Aspire.Hosting/onBeforeResourceStarted", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Subscribes to the ResourceStopped event
    pub fn on_resource_stopped(&self, callback: impl Fn(Vec<Value>) -> Value + Send + Sync + 'static) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        let callback_id = register_callback(callback);
        args.insert("callback".to_string(), Value::String(callback_id));
        let result = self.client.invoke_capability("Aspire.Hosting/onResourceStopped", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Subscribes to the InitializeResource event
    pub fn on_initialize_resource(&self, callback: impl Fn(Vec<Value>) -> Value + Send + Sync + 'static) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        let callback_id = register_callback(callback);
        args.insert("callback".to_string(), Value::String(callback_id));
        let result = self.client.invoke_capability("Aspire.Hosting/onInitializeResource", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Subscribes to the ResourceEndpointsAllocated event
    pub fn on_resource_endpoints_allocated(&self, callback: impl Fn(Vec<Value>) -> Value + Send + Sync + 'static) -> Result<IResourceWithEndpoints, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        let callback_id = register_callback(callback);
        args.insert("callback".to_string(), Value::String(callback_id));
        let result = self.client.invoke_capability("Aspire.Hosting/onResourceEndpointsAllocated", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResourceWithEndpoints::new(handle, self.client.clone()))
    }

    /// Subscribes to the ResourceReady event
    pub fn on_resource_ready(&self, callback: impl Fn(Vec<Value>) -> Value + Send + Sync + 'static) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        let callback_id = register_callback(callback);
        args.insert("callback".to_string(), Value::String(callback_id));
        let result = self.client.invoke_capability("Aspire.Hosting/onResourceReady", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
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

    /// Adds a dependency from a string or another resource
    pub fn with_union_dependency(&self, dependency: Value) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("dependency".to_string(), serde_json::to_value(&dependency).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting.CodeGeneration.Rust.Tests/withUnionDependency", args)?;
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

    /// Adds a label to the resource
    pub fn with_merge_label(&self, label: &str) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("label".to_string(), serde_json::to_value(&label).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting.CodeGeneration.Rust.Tests/withMergeLabel", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Adds a categorized label to the resource
    pub fn with_merge_label_categorized(&self, label: &str, category: &str) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("label".to_string(), serde_json::to_value(&label).unwrap_or(Value::Null));
        args.insert("category".to_string(), serde_json::to_value(&category).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting.CodeGeneration.Rust.Tests/withMergeLabelCategorized", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Configures a named endpoint
    pub fn with_merge_endpoint(&self, endpoint_name: &str, port: f64) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("endpointName".to_string(), serde_json::to_value(&endpoint_name).unwrap_or(Value::Null));
        args.insert("port".to_string(), serde_json::to_value(&port).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting.CodeGeneration.Rust.Tests/withMergeEndpoint", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Configures a named endpoint with scheme
    pub fn with_merge_endpoint_scheme(&self, endpoint_name: &str, port: f64, scheme: &str) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("endpointName".to_string(), serde_json::to_value(&endpoint_name).unwrap_or(Value::Null));
        args.insert("port".to_string(), serde_json::to_value(&port).unwrap_or(Value::Null));
        args.insert("scheme".to_string(), serde_json::to_value(&scheme).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting.CodeGeneration.Rust.Tests/withMergeEndpointScheme", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Configures resource logging
    pub fn with_merge_logging(&self, log_level: &str, enable_console: Option<bool>, max_files: Option<f64>) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("logLevel".to_string(), serde_json::to_value(&log_level).unwrap_or(Value::Null));
        if let Some(ref v) = enable_console {
            args.insert("enableConsole".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        if let Some(ref v) = max_files {
            args.insert("maxFiles".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        let result = self.client.invoke_capability("Aspire.Hosting.CodeGeneration.Rust.Tests/withMergeLogging", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Configures resource logging with file path
    pub fn with_merge_logging_path(&self, log_level: &str, log_path: &str, enable_console: Option<bool>, max_files: Option<f64>) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("logLevel".to_string(), serde_json::to_value(&log_level).unwrap_or(Value::Null));
        args.insert("logPath".to_string(), serde_json::to_value(&log_path).unwrap_or(Value::Null));
        if let Some(ref v) = enable_console {
            args.insert("enableConsole".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        if let Some(ref v) = max_files {
            args.insert("maxFiles".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        let result = self.client.invoke_capability("Aspire.Hosting.CodeGeneration.Rust.Tests/withMergeLoggingPath", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Configures a route
    pub fn with_merge_route(&self, path: &str, method: &str, handler: &str, priority: f64) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("path".to_string(), serde_json::to_value(&path).unwrap_or(Value::Null));
        args.insert("method".to_string(), serde_json::to_value(&method).unwrap_or(Value::Null));
        args.insert("handler".to_string(), serde_json::to_value(&handler).unwrap_or(Value::Null));
        args.insert("priority".to_string(), serde_json::to_value(&priority).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting.CodeGeneration.Rust.Tests/withMergeRoute", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Configures a route with middleware
    pub fn with_merge_route_middleware(&self, path: &str, method: &str, handler: &str, priority: f64, middleware: &str) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("path".to_string(), serde_json::to_value(&path).unwrap_or(Value::Null));
        args.insert("method".to_string(), serde_json::to_value(&method).unwrap_or(Value::Null));
        args.insert("handler".to_string(), serde_json::to_value(&handler).unwrap_or(Value::Null));
        args.insert("priority".to_string(), serde_json::to_value(&priority).unwrap_or(Value::Null));
        args.insert("middleware".to_string(), serde_json::to_value(&middleware).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting.CodeGeneration.Rust.Tests/withMergeRouteMiddleware", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }
}

/// Wrapper for Aspire.Hosting/Aspire.Hosting.ApplicationModel.ExecuteCommandContext
pub struct ExecuteCommandContext {
    handle: Handle,
    client: Arc<AspireClient>,
}

impl HasHandle for ExecuteCommandContext {
    fn handle(&self) -> &Handle {
        &self.handle
    }
}

impl ExecuteCommandContext {
    pub fn new(handle: Handle, client: Arc<AspireClient>) -> Self {
        Self { handle, client }
    }

    pub fn handle(&self) -> &Handle {
        &self.handle
    }

    pub fn client(&self) -> &Arc<AspireClient> {
        &self.client
    }

    /// Gets the ServiceProvider property
    pub fn service_provider(&self) -> Result<IServiceProvider, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("context".to_string(), self.handle.to_json());
        let result = self.client.invoke_capability("Aspire.Hosting.ApplicationModel/ExecuteCommandContext.serviceProvider", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IServiceProvider::new(handle, self.client.clone()))
    }

    /// Sets the ServiceProvider property
    pub fn set_service_provider(&self, value: &IServiceProvider) -> Result<ExecuteCommandContext, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("context".to_string(), self.handle.to_json());
        args.insert("value".to_string(), value.handle().to_json());
        let result = self.client.invoke_capability("Aspire.Hosting.ApplicationModel/ExecuteCommandContext.setServiceProvider", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(ExecuteCommandContext::new(handle, self.client.clone()))
    }

    /// Gets the ResourceName property
    pub fn resource_name(&self) -> Result<String, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("context".to_string(), self.handle.to_json());
        let result = self.client.invoke_capability("Aspire.Hosting.ApplicationModel/ExecuteCommandContext.resourceName", args)?;
        Ok(serde_json::from_value(result)?)
    }

    /// Sets the ResourceName property
    pub fn set_resource_name(&self, value: &str) -> Result<ExecuteCommandContext, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("context".to_string(), self.handle.to_json());
        args.insert("value".to_string(), serde_json::to_value(&value).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting.ApplicationModel/ExecuteCommandContext.setResourceName", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(ExecuteCommandContext::new(handle, self.client.clone()))
    }

    /// Gets the CancellationToken property
    pub fn cancellation_token(&self) -> Result<CancellationToken, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("context".to_string(), self.handle.to_json());
        let result = self.client.invoke_capability("Aspire.Hosting.ApplicationModel/ExecuteCommandContext.cancellationToken", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(CancellationToken::new(handle, self.client.clone()))
    }

    /// Sets the CancellationToken property
    pub fn set_cancellation_token(&self, value: Option<&CancellationToken>) -> Result<ExecuteCommandContext, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("context".to_string(), self.handle.to_json());
        if let Some(token) = value {
            let token_id = register_cancellation(token, self.client.clone());
            args.insert("value".to_string(), Value::String(token_id));
        }
        let result = self.client.invoke_capability("Aspire.Hosting.ApplicationModel/ExecuteCommandContext.setCancellationToken", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(ExecuteCommandContext::new(handle, self.client.clone()))
    }

    /// Gets the Logger property
    pub fn logger(&self) -> Result<ILogger, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("context".to_string(), self.handle.to_json());
        let result = self.client.invoke_capability("Aspire.Hosting.ApplicationModel/ExecuteCommandContext.logger", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(ILogger::new(handle, self.client.clone()))
    }

    /// Sets the Logger property
    pub fn set_logger(&self, value: &ILogger) -> Result<ExecuteCommandContext, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("context".to_string(), self.handle.to_json());
        args.insert("value".to_string(), value.handle().to_json());
        let result = self.client.invoke_capability("Aspire.Hosting.ApplicationModel/ExecuteCommandContext.setLogger", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(ExecuteCommandContext::new(handle, self.client.clone()))
    }
}

/// Wrapper for Aspire.Hosting/Aspire.Hosting.ExternalServiceResource
pub struct ExternalServiceResource {
    handle: Handle,
    client: Arc<AspireClient>,
}

impl HasHandle for ExternalServiceResource {
    fn handle(&self) -> &Handle {
        &self.handle
    }
}

impl ExternalServiceResource {
    pub fn new(handle: Handle, client: Arc<AspireClient>) -> Self {
        Self { handle, client }
    }

    pub fn handle(&self) -> &Handle {
        &self.handle
    }

    pub fn client(&self) -> &Arc<AspireClient> {
        &self.client
    }

    /// Configures a resource to use a container registry
    pub fn with_container_registry(&self, registry: &IResource) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("registry".to_string(), registry.handle().to_json());
        let result = self.client.invoke_capability("Aspire.Hosting/withContainerRegistry", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Sets the base image for a Dockerfile build
    pub fn with_dockerfile_base_image(&self, build_image: Option<&str>, runtime_image: Option<&str>) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        if let Some(ref v) = build_image {
            args.insert("buildImage".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        if let Some(ref v) = runtime_image {
            args.insert("runtimeImage".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        let result = self.client.invoke_capability("Aspire.Hosting/withDockerfileBaseImage", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Adds an HTTP health check to an external service
    pub fn with_external_service_http_health_check(&self, path: Option<&str>, status_code: Option<f64>) -> Result<ExternalServiceResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        if let Some(ref v) = path {
            args.insert("path".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        if let Some(ref v) = status_code {
            args.insert("statusCode".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        let result = self.client.invoke_capability("Aspire.Hosting/withExternalServiceHttpHealthCheck", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(ExternalServiceResource::new(handle, self.client.clone()))
    }

    /// Adds a required command dependency
    pub fn with_required_command(&self, command: &str, help_link: Option<&str>) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("command".to_string(), serde_json::to_value(&command).unwrap_or(Value::Null));
        if let Some(ref v) = help_link {
            args.insert("helpLink".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        let result = self.client.invoke_capability("Aspire.Hosting/withRequiredCommand", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Customizes displayed URLs via callback
    pub fn with_urls_callback(&self, callback: impl Fn(Vec<Value>) -> Value + Send + Sync + 'static) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        let callback_id = register_callback(callback);
        args.insert("callback".to_string(), Value::String(callback_id));
        let result = self.client.invoke_capability("Aspire.Hosting/withUrlsCallback", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Adds or modifies displayed URLs
    pub fn with_url(&self, url: &str, display_text: Option<&str>) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("url".to_string(), serde_json::to_value(&url).unwrap_or(Value::Null));
        if let Some(ref v) = display_text {
            args.insert("displayText".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        let result = self.client.invoke_capability("Aspire.Hosting/withUrl", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Adds a URL using a reference expression
    pub fn with_url_expression(&self, url: ReferenceExpression, display_text: Option<&str>) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("url".to_string(), serde_json::to_value(&url).unwrap_or(Value::Null));
        if let Some(ref v) = display_text {
            args.insert("displayText".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        let result = self.client.invoke_capability("Aspire.Hosting/withUrlExpression", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Customizes the URL for a specific endpoint via callback
    pub fn with_url_for_endpoint(&self, endpoint_name: &str, callback: impl Fn(Vec<Value>) -> Value + Send + Sync + 'static) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("endpointName".to_string(), serde_json::to_value(&endpoint_name).unwrap_or(Value::Null));
        let callback_id = register_callback(callback);
        args.insert("callback".to_string(), Value::String(callback_id));
        let result = self.client.invoke_capability("Aspire.Hosting/withUrlForEndpoint", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Excludes the resource from the deployment manifest
    pub fn exclude_from_manifest(&self) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        let result = self.client.invoke_capability("Aspire.Hosting/excludeFromManifest", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Prevents resource from starting automatically
    pub fn with_explicit_start(&self) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        let result = self.client.invoke_capability("Aspire.Hosting/withExplicitStart", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Adds a health check by key
    pub fn with_health_check(&self, key: &str) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("key".to_string(), serde_json::to_value(&key).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting/withHealthCheck", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Adds a resource command
    pub fn with_command(&self, name: &str, display_name: &str, execute_command: impl Fn(Vec<Value>) -> Value + Send + Sync + 'static, command_options: Option<CommandOptions>) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("name".to_string(), serde_json::to_value(&name).unwrap_or(Value::Null));
        args.insert("displayName".to_string(), serde_json::to_value(&display_name).unwrap_or(Value::Null));
        let callback_id = register_callback(execute_command);
        args.insert("executeCommand".to_string(), Value::String(callback_id));
        if let Some(ref v) = command_options {
            args.insert("commandOptions".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        let result = self.client.invoke_capability("Aspire.Hosting/withCommand", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Sets the parent relationship
    pub fn with_parent_relationship(&self, parent: &IResource) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("parent".to_string(), parent.handle().to_json());
        let result = self.client.invoke_capability("Aspire.Hosting/withBuilderParentRelationship", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Sets a child relationship
    pub fn with_child_relationship(&self, child: &IResource) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("child".to_string(), child.handle().to_json());
        let result = self.client.invoke_capability("Aspire.Hosting/withBuilderChildRelationship", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Sets the icon for the resource
    pub fn with_icon_name(&self, icon_name: &str, icon_variant: Option<IconVariant>) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("iconName".to_string(), serde_json::to_value(&icon_name).unwrap_or(Value::Null));
        if let Some(ref v) = icon_variant {
            args.insert("iconVariant".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        let result = self.client.invoke_capability("Aspire.Hosting/withIconName", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Excludes the resource from MCP server exposure
    pub fn exclude_from_mcp(&self) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        let result = self.client.invoke_capability("Aspire.Hosting/excludeFromMcp", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Adds a pipeline step to the resource
    pub fn with_pipeline_step_factory(&self, step_name: &str, callback: impl Fn(Vec<Value>) -> Value + Send + Sync + 'static, depends_on: Option<Vec<String>>, required_by: Option<Vec<String>>, tags: Option<Vec<String>>, description: Option<&str>) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("stepName".to_string(), serde_json::to_value(&step_name).unwrap_or(Value::Null));
        let callback_id = register_callback(callback);
        args.insert("callback".to_string(), Value::String(callback_id));
        if let Some(ref v) = depends_on {
            args.insert("dependsOn".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        if let Some(ref v) = required_by {
            args.insert("requiredBy".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        if let Some(ref v) = tags {
            args.insert("tags".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        if let Some(ref v) = description {
            args.insert("description".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        let result = self.client.invoke_capability("Aspire.Hosting/withPipelineStepFactory", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Configures pipeline step dependencies via a callback
    pub fn with_pipeline_configuration(&self, callback: impl Fn(Vec<Value>) -> Value + Send + Sync + 'static) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        let callback_id = register_callback(callback);
        args.insert("callback".to_string(), Value::String(callback_id));
        let result = self.client.invoke_capability("Aspire.Hosting/withPipelineConfiguration", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Gets the resource name
    pub fn get_resource_name(&self) -> Result<String, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("resource".to_string(), self.handle.to_json());
        let result = self.client.invoke_capability("Aspire.Hosting/getResourceName", args)?;
        Ok(serde_json::from_value(result)?)
    }

    /// Subscribes to the BeforeResourceStarted event
    pub fn on_before_resource_started(&self, callback: impl Fn(Vec<Value>) -> Value + Send + Sync + 'static) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        let callback_id = register_callback(callback);
        args.insert("callback".to_string(), Value::String(callback_id));
        let result = self.client.invoke_capability("Aspire.Hosting/onBeforeResourceStarted", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Subscribes to the ResourceStopped event
    pub fn on_resource_stopped(&self, callback: impl Fn(Vec<Value>) -> Value + Send + Sync + 'static) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        let callback_id = register_callback(callback);
        args.insert("callback".to_string(), Value::String(callback_id));
        let result = self.client.invoke_capability("Aspire.Hosting/onResourceStopped", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Subscribes to the InitializeResource event
    pub fn on_initialize_resource(&self, callback: impl Fn(Vec<Value>) -> Value + Send + Sync + 'static) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        let callback_id = register_callback(callback);
        args.insert("callback".to_string(), Value::String(callback_id));
        let result = self.client.invoke_capability("Aspire.Hosting/onInitializeResource", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Subscribes to the ResourceReady event
    pub fn on_resource_ready(&self, callback: impl Fn(Vec<Value>) -> Value + Send + Sync + 'static) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        let callback_id = register_callback(callback);
        args.insert("callback".to_string(), Value::String(callback_id));
        let result = self.client.invoke_capability("Aspire.Hosting/onResourceReady", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
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

    /// Adds a dependency from a string or another resource
    pub fn with_union_dependency(&self, dependency: Value) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("dependency".to_string(), serde_json::to_value(&dependency).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting.CodeGeneration.Rust.Tests/withUnionDependency", args)?;
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

    /// Adds a label to the resource
    pub fn with_merge_label(&self, label: &str) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("label".to_string(), serde_json::to_value(&label).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting.CodeGeneration.Rust.Tests/withMergeLabel", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Adds a categorized label to the resource
    pub fn with_merge_label_categorized(&self, label: &str, category: &str) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("label".to_string(), serde_json::to_value(&label).unwrap_or(Value::Null));
        args.insert("category".to_string(), serde_json::to_value(&category).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting.CodeGeneration.Rust.Tests/withMergeLabelCategorized", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Configures a named endpoint
    pub fn with_merge_endpoint(&self, endpoint_name: &str, port: f64) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("endpointName".to_string(), serde_json::to_value(&endpoint_name).unwrap_or(Value::Null));
        args.insert("port".to_string(), serde_json::to_value(&port).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting.CodeGeneration.Rust.Tests/withMergeEndpoint", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Configures a named endpoint with scheme
    pub fn with_merge_endpoint_scheme(&self, endpoint_name: &str, port: f64, scheme: &str) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("endpointName".to_string(), serde_json::to_value(&endpoint_name).unwrap_or(Value::Null));
        args.insert("port".to_string(), serde_json::to_value(&port).unwrap_or(Value::Null));
        args.insert("scheme".to_string(), serde_json::to_value(&scheme).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting.CodeGeneration.Rust.Tests/withMergeEndpointScheme", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Configures resource logging
    pub fn with_merge_logging(&self, log_level: &str, enable_console: Option<bool>, max_files: Option<f64>) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("logLevel".to_string(), serde_json::to_value(&log_level).unwrap_or(Value::Null));
        if let Some(ref v) = enable_console {
            args.insert("enableConsole".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        if let Some(ref v) = max_files {
            args.insert("maxFiles".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        let result = self.client.invoke_capability("Aspire.Hosting.CodeGeneration.Rust.Tests/withMergeLogging", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Configures resource logging with file path
    pub fn with_merge_logging_path(&self, log_level: &str, log_path: &str, enable_console: Option<bool>, max_files: Option<f64>) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("logLevel".to_string(), serde_json::to_value(&log_level).unwrap_or(Value::Null));
        args.insert("logPath".to_string(), serde_json::to_value(&log_path).unwrap_or(Value::Null));
        if let Some(ref v) = enable_console {
            args.insert("enableConsole".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        if let Some(ref v) = max_files {
            args.insert("maxFiles".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        let result = self.client.invoke_capability("Aspire.Hosting.CodeGeneration.Rust.Tests/withMergeLoggingPath", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Configures a route
    pub fn with_merge_route(&self, path: &str, method: &str, handler: &str, priority: f64) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("path".to_string(), serde_json::to_value(&path).unwrap_or(Value::Null));
        args.insert("method".to_string(), serde_json::to_value(&method).unwrap_or(Value::Null));
        args.insert("handler".to_string(), serde_json::to_value(&handler).unwrap_or(Value::Null));
        args.insert("priority".to_string(), serde_json::to_value(&priority).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting.CodeGeneration.Rust.Tests/withMergeRoute", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Configures a route with middleware
    pub fn with_merge_route_middleware(&self, path: &str, method: &str, handler: &str, priority: f64, middleware: &str) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("path".to_string(), serde_json::to_value(&path).unwrap_or(Value::Null));
        args.insert("method".to_string(), serde_json::to_value(&method).unwrap_or(Value::Null));
        args.insert("handler".to_string(), serde_json::to_value(&handler).unwrap_or(Value::Null));
        args.insert("priority".to_string(), serde_json::to_value(&priority).unwrap_or(Value::Null));
        args.insert("middleware".to_string(), serde_json::to_value(&middleware).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting.CodeGeneration.Rust.Tests/withMergeRouteMiddleware", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }
}

/// Wrapper for Aspire.Hosting/Aspire.Hosting.ApplicationModel.IComputeResource
pub struct IComputeResource {
    handle: Handle,
    client: Arc<AspireClient>,
}

impl HasHandle for IComputeResource {
    fn handle(&self) -> &Handle {
        &self.handle
    }
}

impl IComputeResource {
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

/// Wrapper for Microsoft.Extensions.Configuration.Abstractions/Microsoft.Extensions.Configuration.IConfiguration
pub struct IConfiguration {
    handle: Handle,
    client: Arc<AspireClient>,
}

impl HasHandle for IConfiguration {
    fn handle(&self) -> &Handle {
        &self.handle
    }
}

impl IConfiguration {
    pub fn new(handle: Handle, client: Arc<AspireClient>) -> Self {
        Self { handle, client }
    }

    pub fn handle(&self) -> &Handle {
        &self.handle
    }

    pub fn client(&self) -> &Arc<AspireClient> {
        &self.client
    }

    /// Gets a configuration value by key
    pub fn get_config_value(&self, key: &str) -> Result<String, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("configuration".to_string(), self.handle.to_json());
        args.insert("key".to_string(), serde_json::to_value(&key).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting/getConfigValue", args)?;
        Ok(serde_json::from_value(result)?)
    }

    /// Gets a connection string by name
    pub fn get_connection_string(&self, name: &str) -> Result<String, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("configuration".to_string(), self.handle.to_json());
        args.insert("name".to_string(), serde_json::to_value(&name).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting/getConnectionString", args)?;
        Ok(serde_json::from_value(result)?)
    }

    /// Gets a configuration section by key
    pub fn get_section(&self, key: &str) -> Result<IConfigurationSection, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("configuration".to_string(), self.handle.to_json());
        args.insert("key".to_string(), serde_json::to_value(&key).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting/getSection", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IConfigurationSection::new(handle, self.client.clone()))
    }

    /// Gets child configuration sections
    pub fn get_children(&self) -> Result<Vec<IConfigurationSection>, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("configuration".to_string(), self.handle.to_json());
        let result = self.client.invoke_capability("Aspire.Hosting/getChildren", args)?;
        let handles: Vec<Handle> = serde_json::from_value(result)?;
        Ok(handles.into_iter().map(|h| IConfigurationSection::new(h, self.client.clone())).collect())
    }

    /// Checks whether a configuration section exists
    pub fn exists(&self, key: &str) -> Result<bool, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("configuration".to_string(), self.handle.to_json());
        args.insert("key".to_string(), serde_json::to_value(&key).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting/exists", args)?;
        Ok(serde_json::from_value(result)?)
    }
}

/// Wrapper for Microsoft.Extensions.Configuration.Abstractions/Microsoft.Extensions.Configuration.IConfigurationSection
pub struct IConfigurationSection {
    handle: Handle,
    client: Arc<AspireClient>,
}

impl HasHandle for IConfigurationSection {
    fn handle(&self) -> &Handle {
        &self.handle
    }
}

impl IConfigurationSection {
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

/// Wrapper for Aspire.Hosting/Aspire.Hosting.ApplicationModel.IContainerFilesDestinationResource
pub struct IContainerFilesDestinationResource {
    handle: Handle,
    client: Arc<AspireClient>,
}

impl HasHandle for IContainerFilesDestinationResource {
    fn handle(&self) -> &Handle {
        &self.handle
    }
}

impl IContainerFilesDestinationResource {
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

    /// Adds a connection string with a reference expression
    pub fn add_connection_string_expression(&self, name: &str, connection_string_expression: ReferenceExpression) -> Result<ConnectionStringResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("name".to_string(), serde_json::to_value(&name).unwrap_or(Value::Null));
        args.insert("connectionStringExpression".to_string(), serde_json::to_value(&connection_string_expression).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting/addConnectionStringExpression", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(ConnectionStringResource::new(handle, self.client.clone()))
    }

    /// Adds a connection string with a builder callback
    pub fn add_connection_string_builder(&self, name: &str, connection_string_builder: impl Fn(Vec<Value>) -> Value + Send + Sync + 'static) -> Result<ConnectionStringResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("name".to_string(), serde_json::to_value(&name).unwrap_or(Value::Null));
        let callback_id = register_callback(connection_string_builder);
        args.insert("connectionStringBuilder".to_string(), Value::String(callback_id));
        let result = self.client.invoke_capability("Aspire.Hosting/addConnectionStringBuilder", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(ConnectionStringResource::new(handle, self.client.clone()))
    }

    /// Adds a container registry resource
    pub fn add_container_registry(&self, name: &str, endpoint: &ParameterResource, repository: Option<&ParameterResource>) -> Result<ContainerRegistryResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("name".to_string(), serde_json::to_value(&name).unwrap_or(Value::Null));
        args.insert("endpoint".to_string(), endpoint.handle().to_json());
        if let Some(ref v) = repository {
            args.insert("repository".to_string(), v.handle().to_json());
        }
        let result = self.client.invoke_capability("Aspire.Hosting/addContainerRegistry", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(ContainerRegistryResource::new(handle, self.client.clone()))
    }

    /// Adds a container registry with string endpoint
    pub fn add_container_registry_from_string(&self, name: &str, endpoint: &str, repository: Option<&str>) -> Result<ContainerRegistryResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("name".to_string(), serde_json::to_value(&name).unwrap_or(Value::Null));
        args.insert("endpoint".to_string(), serde_json::to_value(&endpoint).unwrap_or(Value::Null));
        if let Some(ref v) = repository {
            args.insert("repository".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        let result = self.client.invoke_capability("Aspire.Hosting/addContainerRegistryFromString", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(ContainerRegistryResource::new(handle, self.client.clone()))
    }

    /// Adds a container resource
    pub fn add_container(&self, name: &str, image: &str) -> Result<ContainerResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("name".to_string(), serde_json::to_value(&name).unwrap_or(Value::Null));
        args.insert("image".to_string(), serde_json::to_value(&image).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting/addContainer", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(ContainerResource::new(handle, self.client.clone()))
    }

    /// Adds a container resource built from a Dockerfile
    pub fn add_dockerfile(&self, name: &str, context_path: &str, dockerfile_path: Option<&str>, stage: Option<&str>) -> Result<ContainerResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("name".to_string(), serde_json::to_value(&name).unwrap_or(Value::Null));
        args.insert("contextPath".to_string(), serde_json::to_value(&context_path).unwrap_or(Value::Null));
        if let Some(ref v) = dockerfile_path {
            args.insert("dockerfilePath".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        if let Some(ref v) = stage {
            args.insert("stage".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        let result = self.client.invoke_capability("Aspire.Hosting/addDockerfile", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(ContainerResource::new(handle, self.client.clone()))
    }

    /// Adds a .NET tool resource
    pub fn add_dotnet_tool(&self, name: &str, package_id: &str) -> Result<DotnetToolResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("name".to_string(), serde_json::to_value(&name).unwrap_or(Value::Null));
        args.insert("packageId".to_string(), serde_json::to_value(&package_id).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting/addDotnetTool", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(DotnetToolResource::new(handle, self.client.clone()))
    }

    /// Adds an executable resource
    pub fn add_executable(&self, name: &str, command: &str, working_directory: &str, args: Vec<String>) -> Result<ExecutableResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("name".to_string(), serde_json::to_value(&name).unwrap_or(Value::Null));
        args.insert("command".to_string(), serde_json::to_value(&command).unwrap_or(Value::Null));
        args.insert("workingDirectory".to_string(), serde_json::to_value(&working_directory).unwrap_or(Value::Null));
        args.insert("args".to_string(), serde_json::to_value(&args).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting/addExecutable", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(ExecutableResource::new(handle, self.client.clone()))
    }

    /// Adds an external service resource
    pub fn add_external_service(&self, name: &str, url: &str) -> Result<ExternalServiceResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("name".to_string(), serde_json::to_value(&name).unwrap_or(Value::Null));
        args.insert("url".to_string(), serde_json::to_value(&url).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting/addExternalService", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(ExternalServiceResource::new(handle, self.client.clone()))
    }

    /// Adds an external service with a URI
    pub fn add_external_service_uri(&self, name: &str, uri: &str) -> Result<ExternalServiceResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("name".to_string(), serde_json::to_value(&name).unwrap_or(Value::Null));
        args.insert("uri".to_string(), serde_json::to_value(&uri).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting/addExternalServiceUri", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(ExternalServiceResource::new(handle, self.client.clone()))
    }

    /// Adds an external service with a parameter URL
    pub fn add_external_service_parameter(&self, name: &str, url_parameter: &ParameterResource) -> Result<ExternalServiceResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("name".to_string(), serde_json::to_value(&name).unwrap_or(Value::Null));
        args.insert("urlParameter".to_string(), url_parameter.handle().to_json());
        let result = self.client.invoke_capability("Aspire.Hosting/addExternalServiceParameter", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(ExternalServiceResource::new(handle, self.client.clone()))
    }

    /// Gets the AppHostDirectory property
    pub fn app_host_directory(&self) -> Result<String, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("context".to_string(), self.handle.to_json());
        let result = self.client.invoke_capability("Aspire.Hosting/IDistributedApplicationBuilder.appHostDirectory", args)?;
        Ok(serde_json::from_value(result)?)
    }

    /// Gets the Environment property
    pub fn environment(&self) -> Result<IHostEnvironment, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("context".to_string(), self.handle.to_json());
        let result = self.client.invoke_capability("Aspire.Hosting/IDistributedApplicationBuilder.environment", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IHostEnvironment::new(handle, self.client.clone()))
    }

    /// Gets the Eventing property
    pub fn eventing(&self) -> Result<IDistributedApplicationEventing, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("context".to_string(), self.handle.to_json());
        let result = self.client.invoke_capability("Aspire.Hosting/IDistributedApplicationBuilder.eventing", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IDistributedApplicationEventing::new(handle, self.client.clone()))
    }

    /// Gets the ExecutionContext property
    pub fn execution_context(&self) -> Result<DistributedApplicationExecutionContext, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("context".to_string(), self.handle.to_json());
        let result = self.client.invoke_capability("Aspire.Hosting/IDistributedApplicationBuilder.executionContext", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(DistributedApplicationExecutionContext::new(handle, self.client.clone()))
    }

    /// Gets the UserSecretsManager property
    pub fn user_secrets_manager(&self) -> Result<IUserSecretsManager, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("context".to_string(), self.handle.to_json());
        let result = self.client.invoke_capability("Aspire.Hosting/IDistributedApplicationBuilder.userSecretsManager", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IUserSecretsManager::new(handle, self.client.clone()))
    }

    /// Builds the distributed application
    pub fn build(&self) -> Result<DistributedApplication, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("context".to_string(), self.handle.to_json());
        let result = self.client.invoke_capability("Aspire.Hosting/build", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(DistributedApplication::new(handle, self.client.clone()))
    }

    /// Adds a parameter resource
    pub fn add_parameter(&self, name: &str, secret: Option<bool>) -> Result<ParameterResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("name".to_string(), serde_json::to_value(&name).unwrap_or(Value::Null));
        if let Some(ref v) = secret {
            args.insert("secret".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        let result = self.client.invoke_capability("Aspire.Hosting/addParameter", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(ParameterResource::new(handle, self.client.clone()))
    }

    /// Adds a parameter with a default value
    pub fn add_parameter_with_value(&self, name: &str, value: &str, publish_value_as_default: Option<bool>, secret: Option<bool>) -> Result<ParameterResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("name".to_string(), serde_json::to_value(&name).unwrap_or(Value::Null));
        args.insert("value".to_string(), serde_json::to_value(&value).unwrap_or(Value::Null));
        if let Some(ref v) = publish_value_as_default {
            args.insert("publishValueAsDefault".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        if let Some(ref v) = secret {
            args.insert("secret".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        let result = self.client.invoke_capability("Aspire.Hosting/addParameterWithValue", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(ParameterResource::new(handle, self.client.clone()))
    }

    /// Adds a parameter sourced from configuration
    pub fn add_parameter_from_configuration(&self, name: &str, configuration_key: &str, secret: Option<bool>) -> Result<ParameterResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("name".to_string(), serde_json::to_value(&name).unwrap_or(Value::Null));
        args.insert("configurationKey".to_string(), serde_json::to_value(&configuration_key).unwrap_or(Value::Null));
        if let Some(ref v) = secret {
            args.insert("secret".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        let result = self.client.invoke_capability("Aspire.Hosting/addParameterFromConfiguration", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(ParameterResource::new(handle, self.client.clone()))
    }

    /// Adds a connection string resource
    pub fn add_connection_string(&self, name: &str, environment_variable_name: Option<&str>) -> Result<IResourceWithConnectionString, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("name".to_string(), serde_json::to_value(&name).unwrap_or(Value::Null));
        if let Some(ref v) = environment_variable_name {
            args.insert("environmentVariableName".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        let result = self.client.invoke_capability("Aspire.Hosting/addConnectionString", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResourceWithConnectionString::new(handle, self.client.clone()))
    }

    /// Adds a .NET project resource
    pub fn add_project(&self, name: &str, project_path: &str, launch_profile_name: &str) -> Result<ProjectResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("name".to_string(), serde_json::to_value(&name).unwrap_or(Value::Null));
        args.insert("projectPath".to_string(), serde_json::to_value(&project_path).unwrap_or(Value::Null));
        args.insert("launchProfileName".to_string(), serde_json::to_value(&launch_profile_name).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting/addProject", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(ProjectResource::new(handle, self.client.clone()))
    }

    /// Adds a project resource with configuration options
    pub fn add_project_with_options(&self, name: &str, project_path: &str, configure: impl Fn(Vec<Value>) -> Value + Send + Sync + 'static) -> Result<ProjectResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("name".to_string(), serde_json::to_value(&name).unwrap_or(Value::Null));
        args.insert("projectPath".to_string(), serde_json::to_value(&project_path).unwrap_or(Value::Null));
        let callback_id = register_callback(configure);
        args.insert("configure".to_string(), Value::String(callback_id));
        let result = self.client.invoke_capability("Aspire.Hosting/addProjectWithOptions", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(ProjectResource::new(handle, self.client.clone()))
    }

    /// Adds a C# application resource
    pub fn add_c_sharp_app(&self, name: &str, path: &str) -> Result<ProjectResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("name".to_string(), serde_json::to_value(&name).unwrap_or(Value::Null));
        args.insert("path".to_string(), serde_json::to_value(&path).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting/addCSharpApp", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(ProjectResource::new(handle, self.client.clone()))
    }

    /// Adds a C# application resource with configuration options
    pub fn add_c_sharp_app_with_options(&self, name: &str, path: &str, configure: impl Fn(Vec<Value>) -> Value + Send + Sync + 'static) -> Result<CSharpAppResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("name".to_string(), serde_json::to_value(&name).unwrap_or(Value::Null));
        args.insert("path".to_string(), serde_json::to_value(&path).unwrap_or(Value::Null));
        let callback_id = register_callback(configure);
        args.insert("configure".to_string(), Value::String(callback_id));
        let result = self.client.invoke_capability("Aspire.Hosting/addCSharpAppWithOptions", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(CSharpAppResource::new(handle, self.client.clone()))
    }

    /// Gets the application configuration
    pub fn get_configuration(&self) -> Result<IConfiguration, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        let result = self.client.invoke_capability("Aspire.Hosting/getConfiguration", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IConfiguration::new(handle, self.client.clone()))
    }

    /// Subscribes to the BeforeStart event
    pub fn subscribe_before_start(&self, callback: impl Fn(Vec<Value>) -> Value + Send + Sync + 'static) -> Result<DistributedApplicationEventSubscription, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        let callback_id = register_callback(callback);
        args.insert("callback".to_string(), Value::String(callback_id));
        let result = self.client.invoke_capability("Aspire.Hosting/subscribeBeforeStart", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(DistributedApplicationEventSubscription::new(handle, self.client.clone()))
    }

    /// Subscribes to the AfterResourcesCreated event
    pub fn subscribe_after_resources_created(&self, callback: impl Fn(Vec<Value>) -> Value + Send + Sync + 'static) -> Result<DistributedApplicationEventSubscription, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        let callback_id = register_callback(callback);
        args.insert("callback".to_string(), Value::String(callback_id));
        let result = self.client.invoke_capability("Aspire.Hosting/subscribeAfterResourcesCreated", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(DistributedApplicationEventSubscription::new(handle, self.client.clone()))
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

/// Wrapper for Aspire.Hosting/Aspire.Hosting.Eventing.IDistributedApplicationEvent
pub struct IDistributedApplicationEvent {
    handle: Handle,
    client: Arc<AspireClient>,
}

impl HasHandle for IDistributedApplicationEvent {
    fn handle(&self) -> &Handle {
        &self.handle
    }
}

impl IDistributedApplicationEvent {
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

/// Wrapper for Aspire.Hosting/Aspire.Hosting.Eventing.IDistributedApplicationEventing
pub struct IDistributedApplicationEventing {
    handle: Handle,
    client: Arc<AspireClient>,
}

impl HasHandle for IDistributedApplicationEventing {
    fn handle(&self) -> &Handle {
        &self.handle
    }
}

impl IDistributedApplicationEventing {
    pub fn new(handle: Handle, client: Arc<AspireClient>) -> Self {
        Self { handle, client }
    }

    pub fn handle(&self) -> &Handle {
        &self.handle
    }

    pub fn client(&self) -> &Arc<AspireClient> {
        &self.client
    }

    /// Invokes the Unsubscribe method
    pub fn unsubscribe(&self, subscription: &DistributedApplicationEventSubscription) -> Result<(), Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("context".to_string(), self.handle.to_json());
        args.insert("subscription".to_string(), subscription.handle().to_json());
        let result = self.client.invoke_capability("Aspire.Hosting.Eventing/IDistributedApplicationEventing.unsubscribe", args)?;
        Ok(())
    }
}

/// Wrapper for Aspire.Hosting/Aspire.Hosting.Eventing.IDistributedApplicationResourceEvent
pub struct IDistributedApplicationResourceEvent {
    handle: Handle,
    client: Arc<AspireClient>,
}

impl HasHandle for IDistributedApplicationResourceEvent {
    fn handle(&self) -> &Handle {
        &self.handle
    }
}

impl IDistributedApplicationResourceEvent {
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

/// Wrapper for Aspire.Hosting/Aspire.Hosting.ApplicationModel.IExpressionValue
pub struct IExpressionValue {
    handle: Handle,
    client: Arc<AspireClient>,
}

impl HasHandle for IExpressionValue {
    fn handle(&self) -> &Handle {
        &self.handle
    }
}

impl IExpressionValue {
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

/// Wrapper for Microsoft.Extensions.Hosting.Abstractions/Microsoft.Extensions.Hosting.IHostEnvironment
pub struct IHostEnvironment {
    handle: Handle,
    client: Arc<AspireClient>,
}

impl HasHandle for IHostEnvironment {
    fn handle(&self) -> &Handle {
        &self.handle
    }
}

impl IHostEnvironment {
    pub fn new(handle: Handle, client: Arc<AspireClient>) -> Self {
        Self { handle, client }
    }

    pub fn handle(&self) -> &Handle {
        &self.handle
    }

    pub fn client(&self) -> &Arc<AspireClient> {
        &self.client
    }

    /// Checks if running in Development environment
    pub fn is_development(&self) -> Result<bool, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("environment".to_string(), self.handle.to_json());
        let result = self.client.invoke_capability("Aspire.Hosting/isDevelopment", args)?;
        Ok(serde_json::from_value(result)?)
    }

    /// Checks if running in Production environment
    pub fn is_production(&self) -> Result<bool, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("environment".to_string(), self.handle.to_json());
        let result = self.client.invoke_capability("Aspire.Hosting/isProduction", args)?;
        Ok(serde_json::from_value(result)?)
    }

    /// Checks if running in Staging environment
    pub fn is_staging(&self) -> Result<bool, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("environment".to_string(), self.handle.to_json());
        let result = self.client.invoke_capability("Aspire.Hosting/isStaging", args)?;
        Ok(serde_json::from_value(result)?)
    }

    /// Checks if the environment matches the specified name
    pub fn is_environment(&self, environment_name: &str) -> Result<bool, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("environment".to_string(), self.handle.to_json());
        args.insert("environmentName".to_string(), serde_json::to_value(&environment_name).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting/isEnvironment", args)?;
        Ok(serde_json::from_value(result)?)
    }
}

/// Wrapper for Microsoft.Extensions.Logging.Abstractions/Microsoft.Extensions.Logging.ILogger
pub struct ILogger {
    handle: Handle,
    client: Arc<AspireClient>,
}

impl HasHandle for ILogger {
    fn handle(&self) -> &Handle {
        &self.handle
    }
}

impl ILogger {
    pub fn new(handle: Handle, client: Arc<AspireClient>) -> Self {
        Self { handle, client }
    }

    pub fn handle(&self) -> &Handle {
        &self.handle
    }

    pub fn client(&self) -> &Arc<AspireClient> {
        &self.client
    }

    /// Logs an information message
    pub fn log_information(&self, message: &str) -> Result<(), Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("logger".to_string(), self.handle.to_json());
        args.insert("message".to_string(), serde_json::to_value(&message).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting/logInformation", args)?;
        Ok(())
    }

    /// Logs a warning message
    pub fn log_warning(&self, message: &str) -> Result<(), Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("logger".to_string(), self.handle.to_json());
        args.insert("message".to_string(), serde_json::to_value(&message).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting/logWarning", args)?;
        Ok(())
    }

    /// Logs an error message
    pub fn log_error(&self, message: &str) -> Result<(), Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("logger".to_string(), self.handle.to_json());
        args.insert("message".to_string(), serde_json::to_value(&message).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting/logError", args)?;
        Ok(())
    }

    /// Logs a debug message
    pub fn log_debug(&self, message: &str) -> Result<(), Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("logger".to_string(), self.handle.to_json());
        args.insert("message".to_string(), serde_json::to_value(&message).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting/logDebug", args)?;
        Ok(())
    }

    /// Logs a message with specified level
    pub fn log(&self, level: &str, message: &str) -> Result<(), Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("logger".to_string(), self.handle.to_json());
        args.insert("level".to_string(), serde_json::to_value(&level).unwrap_or(Value::Null));
        args.insert("message".to_string(), serde_json::to_value(&message).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting/log", args)?;
        Ok(())
    }
}

/// Wrapper for Microsoft.Extensions.Logging.Abstractions/Microsoft.Extensions.Logging.ILoggerFactory
pub struct ILoggerFactory {
    handle: Handle,
    client: Arc<AspireClient>,
}

impl HasHandle for ILoggerFactory {
    fn handle(&self) -> &Handle {
        &self.handle
    }
}

impl ILoggerFactory {
    pub fn new(handle: Handle, client: Arc<AspireClient>) -> Self {
        Self { handle, client }
    }

    pub fn handle(&self) -> &Handle {
        &self.handle
    }

    pub fn client(&self) -> &Arc<AspireClient> {
        &self.client
    }

    /// Creates a logger for a category
    pub fn create_logger(&self, category_name: &str) -> Result<ILogger, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("loggerFactory".to_string(), self.handle.to_json());
        args.insert("categoryName".to_string(), serde_json::to_value(&category_name).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting/createLogger", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(ILogger::new(handle, self.client.clone()))
    }
}

/// Wrapper for Aspire.Hosting/Aspire.Hosting.Pipelines.IReportingStep
pub struct IReportingStep {
    handle: Handle,
    client: Arc<AspireClient>,
}

impl HasHandle for IReportingStep {
    fn handle(&self) -> &Handle {
        &self.handle
    }
}

impl IReportingStep {
    pub fn new(handle: Handle, client: Arc<AspireClient>) -> Self {
        Self { handle, client }
    }

    pub fn handle(&self) -> &Handle {
        &self.handle
    }

    pub fn client(&self) -> &Arc<AspireClient> {
        &self.client
    }

    /// Creates a reporting task with plain-text status text
    pub fn create_task(&self, status_text: &str, cancellation_token: Option<&CancellationToken>) -> Result<IReportingTask, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("reportingStep".to_string(), self.handle.to_json());
        args.insert("statusText".to_string(), serde_json::to_value(&status_text).unwrap_or(Value::Null));
        if let Some(token) = cancellation_token {
            let token_id = register_cancellation(token, self.client.clone());
            args.insert("cancellationToken".to_string(), Value::String(token_id));
        }
        let result = self.client.invoke_capability("Aspire.Hosting/createTask", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IReportingTask::new(handle, self.client.clone()))
    }

    /// Creates a reporting task with Markdown-formatted status text
    pub fn create_markdown_task(&self, markdown_string: &str, cancellation_token: Option<&CancellationToken>) -> Result<IReportingTask, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("reportingStep".to_string(), self.handle.to_json());
        args.insert("markdownString".to_string(), serde_json::to_value(&markdown_string).unwrap_or(Value::Null));
        if let Some(token) = cancellation_token {
            let token_id = register_cancellation(token, self.client.clone());
            args.insert("cancellationToken".to_string(), Value::String(token_id));
        }
        let result = self.client.invoke_capability("Aspire.Hosting/createMarkdownTask", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IReportingTask::new(handle, self.client.clone()))
    }

    /// Logs a plain-text message for the reporting step
    pub fn log_step(&self, level: &str, message: &str) -> Result<(), Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("reportingStep".to_string(), self.handle.to_json());
        args.insert("level".to_string(), serde_json::to_value(&level).unwrap_or(Value::Null));
        args.insert("message".to_string(), serde_json::to_value(&message).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting/logStep", args)?;
        Ok(())
    }

    /// Logs a Markdown-formatted message for the reporting step
    pub fn log_step_markdown(&self, level: &str, markdown_string: &str) -> Result<(), Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("reportingStep".to_string(), self.handle.to_json());
        args.insert("level".to_string(), serde_json::to_value(&level).unwrap_or(Value::Null));
        args.insert("markdownString".to_string(), serde_json::to_value(&markdown_string).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting/logStepMarkdown", args)?;
        Ok(())
    }

    /// Completes the reporting step with plain-text completion text
    pub fn complete_step(&self, completion_text: &str, completion_state: Option<&str>, cancellation_token: Option<&CancellationToken>) -> Result<(), Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("reportingStep".to_string(), self.handle.to_json());
        args.insert("completionText".to_string(), serde_json::to_value(&completion_text).unwrap_or(Value::Null));
        if let Some(ref v) = completion_state {
            args.insert("completionState".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        if let Some(token) = cancellation_token {
            let token_id = register_cancellation(token, self.client.clone());
            args.insert("cancellationToken".to_string(), Value::String(token_id));
        }
        let result = self.client.invoke_capability("Aspire.Hosting/completeStep", args)?;
        Ok(())
    }

    /// Completes the reporting step with Markdown-formatted completion text
    pub fn complete_step_markdown(&self, markdown_string: &str, completion_state: Option<&str>, cancellation_token: Option<&CancellationToken>) -> Result<(), Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("reportingStep".to_string(), self.handle.to_json());
        args.insert("markdownString".to_string(), serde_json::to_value(&markdown_string).unwrap_or(Value::Null));
        if let Some(ref v) = completion_state {
            args.insert("completionState".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        if let Some(token) = cancellation_token {
            let token_id = register_cancellation(token, self.client.clone());
            args.insert("cancellationToken".to_string(), Value::String(token_id));
        }
        let result = self.client.invoke_capability("Aspire.Hosting/completeStepMarkdown", args)?;
        Ok(())
    }
}

/// Wrapper for Aspire.Hosting/Aspire.Hosting.Pipelines.IReportingTask
pub struct IReportingTask {
    handle: Handle,
    client: Arc<AspireClient>,
}

impl HasHandle for IReportingTask {
    fn handle(&self) -> &Handle {
        &self.handle
    }
}

impl IReportingTask {
    pub fn new(handle: Handle, client: Arc<AspireClient>) -> Self {
        Self { handle, client }
    }

    pub fn handle(&self) -> &Handle {
        &self.handle
    }

    pub fn client(&self) -> &Arc<AspireClient> {
        &self.client
    }

    /// Updates the reporting task with plain-text status text
    pub fn update_task(&self, status_text: &str, cancellation_token: Option<&CancellationToken>) -> Result<(), Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("reportingTask".to_string(), self.handle.to_json());
        args.insert("statusText".to_string(), serde_json::to_value(&status_text).unwrap_or(Value::Null));
        if let Some(token) = cancellation_token {
            let token_id = register_cancellation(token, self.client.clone());
            args.insert("cancellationToken".to_string(), Value::String(token_id));
        }
        let result = self.client.invoke_capability("Aspire.Hosting/updateTask", args)?;
        Ok(())
    }

    /// Updates the reporting task with Markdown-formatted status text
    pub fn update_task_markdown(&self, markdown_string: &str, cancellation_token: Option<&CancellationToken>) -> Result<(), Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("reportingTask".to_string(), self.handle.to_json());
        args.insert("markdownString".to_string(), serde_json::to_value(&markdown_string).unwrap_or(Value::Null));
        if let Some(token) = cancellation_token {
            let token_id = register_cancellation(token, self.client.clone());
            args.insert("cancellationToken".to_string(), Value::String(token_id));
        }
        let result = self.client.invoke_capability("Aspire.Hosting/updateTaskMarkdown", args)?;
        Ok(())
    }

    /// Completes the reporting task with plain-text completion text
    pub fn complete_task(&self, completion_message: Option<&str>, completion_state: Option<&str>, cancellation_token: Option<&CancellationToken>) -> Result<(), Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("reportingTask".to_string(), self.handle.to_json());
        if let Some(ref v) = completion_message {
            args.insert("completionMessage".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        if let Some(ref v) = completion_state {
            args.insert("completionState".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        if let Some(token) = cancellation_token {
            let token_id = register_cancellation(token, self.client.clone());
            args.insert("cancellationToken".to_string(), Value::String(token_id));
        }
        let result = self.client.invoke_capability("Aspire.Hosting/completeTask", args)?;
        Ok(())
    }

    /// Completes the reporting task with Markdown-formatted completion text
    pub fn complete_task_markdown(&self, markdown_string: &str, completion_state: Option<&str>, cancellation_token: Option<&CancellationToken>) -> Result<(), Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("reportingTask".to_string(), self.handle.to_json());
        args.insert("markdownString".to_string(), serde_json::to_value(&markdown_string).unwrap_or(Value::Null));
        if let Some(ref v) = completion_state {
            args.insert("completionState".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        if let Some(token) = cancellation_token {
            let token_id = register_cancellation(token, self.client.clone());
            args.insert("cancellationToken".to_string(), Value::String(token_id));
        }
        let result = self.client.invoke_capability("Aspire.Hosting/completeTaskMarkdown", args)?;
        Ok(())
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

/// Wrapper for Aspire.Hosting/Aspire.Hosting.ApplicationModel.IResourceWithArgs
pub struct IResourceWithArgs {
    handle: Handle,
    client: Arc<AspireClient>,
}

impl HasHandle for IResourceWithArgs {
    fn handle(&self) -> &Handle {
        &self.handle
    }
}

impl IResourceWithArgs {
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

/// Wrapper for Aspire.Hosting/Aspire.Hosting.IResourceWithContainerFiles
pub struct IResourceWithContainerFiles {
    handle: Handle,
    client: Arc<AspireClient>,
}

impl HasHandle for IResourceWithContainerFiles {
    fn handle(&self) -> &Handle {
        &self.handle
    }
}

impl IResourceWithContainerFiles {
    pub fn new(handle: Handle, client: Arc<AspireClient>) -> Self {
        Self { handle, client }
    }

    pub fn handle(&self) -> &Handle {
        &self.handle
    }

    pub fn client(&self) -> &Arc<AspireClient> {
        &self.client
    }

    /// Sets the source directory for container files
    pub fn with_container_files_source(&self, source_path: &str) -> Result<IResourceWithContainerFiles, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("sourcePath".to_string(), serde_json::to_value(&source_path).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting/withContainerFilesSource", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResourceWithContainerFiles::new(handle, self.client.clone()))
    }

    /// Clears all container file sources
    pub fn clear_container_files_sources(&self) -> Result<IResourceWithContainerFiles, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        let result = self.client.invoke_capability("Aspire.Hosting/clearContainerFilesSources", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResourceWithContainerFiles::new(handle, self.client.clone()))
    }
}

/// Wrapper for Aspire.Hosting/Aspire.Hosting.ApplicationModel.IResourceWithEndpoints
pub struct IResourceWithEndpoints {
    handle: Handle,
    client: Arc<AspireClient>,
}

impl HasHandle for IResourceWithEndpoints {
    fn handle(&self) -> &Handle {
        &self.handle
    }
}

impl IResourceWithEndpoints {
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

/// Wrapper for Aspire.Hosting/Aspire.Hosting.ApplicationModel.IResourceWithParent
pub struct IResourceWithParent {
    handle: Handle,
    client: Arc<AspireClient>,
}

impl HasHandle for IResourceWithParent {
    fn handle(&self) -> &Handle {
        &self.handle
    }
}

impl IResourceWithParent {
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

/// Wrapper for Aspire.Hosting/Aspire.Hosting.ApplicationModel.IResourceWithWaitSupport
pub struct IResourceWithWaitSupport {
    handle: Handle,
    client: Arc<AspireClient>,
}

impl HasHandle for IResourceWithWaitSupport {
    fn handle(&self) -> &Handle {
        &self.handle
    }
}

impl IResourceWithWaitSupport {
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

/// Wrapper for System.ComponentModel/System.IServiceProvider
pub struct IServiceProvider {
    handle: Handle,
    client: Arc<AspireClient>,
}

impl HasHandle for IServiceProvider {
    fn handle(&self) -> &Handle {
        &self.handle
    }
}

impl IServiceProvider {
    pub fn new(handle: Handle, client: Arc<AspireClient>) -> Self {
        Self { handle, client }
    }

    pub fn handle(&self) -> &Handle {
        &self.handle
    }

    pub fn client(&self) -> &Arc<AspireClient> {
        &self.client
    }

    /// Gets the distributed application eventing service from the service provider
    pub fn get_eventing(&self) -> Result<IDistributedApplicationEventing, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("serviceProvider".to_string(), self.handle.to_json());
        let result = self.client.invoke_capability("Aspire.Hosting/getEventing", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IDistributedApplicationEventing::new(handle, self.client.clone()))
    }

    /// Gets the logger factory from the service provider
    pub fn get_logger_factory(&self) -> Result<ILoggerFactory, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("serviceProvider".to_string(), self.handle.to_json());
        let result = self.client.invoke_capability("Aspire.Hosting/getLoggerFactory", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(ILoggerFactory::new(handle, self.client.clone()))
    }

    /// Gets the resource logger service from the service provider
    pub fn get_resource_logger_service(&self) -> Result<ResourceLoggerService, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("serviceProvider".to_string(), self.handle.to_json());
        let result = self.client.invoke_capability("Aspire.Hosting/getResourceLoggerService", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(ResourceLoggerService::new(handle, self.client.clone()))
    }

    /// Gets the distributed application model from the service provider
    pub fn get_distributed_application_model(&self) -> Result<DistributedApplicationModel, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("serviceProvider".to_string(), self.handle.to_json());
        let result = self.client.invoke_capability("Aspire.Hosting/getDistributedApplicationModel", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(DistributedApplicationModel::new(handle, self.client.clone()))
    }

    /// Gets the resource notification service from the service provider
    pub fn get_resource_notification_service(&self) -> Result<ResourceNotificationService, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("serviceProvider".to_string(), self.handle.to_json());
        let result = self.client.invoke_capability("Aspire.Hosting/getResourceNotificationService", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(ResourceNotificationService::new(handle, self.client.clone()))
    }

    /// Gets the user secrets manager from the service provider
    pub fn get_user_secrets_manager(&self) -> Result<IUserSecretsManager, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("serviceProvider".to_string(), self.handle.to_json());
        let result = self.client.invoke_capability("Aspire.Hosting/getUserSecretsManager", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IUserSecretsManager::new(handle, self.client.clone()))
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

/// Wrapper for Aspire.Hosting/Aspire.Hosting.IUserSecretsManager
pub struct IUserSecretsManager {
    handle: Handle,
    client: Arc<AspireClient>,
}

impl HasHandle for IUserSecretsManager {
    fn handle(&self) -> &Handle {
        &self.handle
    }
}

impl IUserSecretsManager {
    pub fn new(handle: Handle, client: Arc<AspireClient>) -> Self {
        Self { handle, client }
    }

    pub fn handle(&self) -> &Handle {
        &self.handle
    }

    pub fn client(&self) -> &Arc<AspireClient> {
        &self.client
    }

    /// Gets the IsAvailable property
    pub fn is_available(&self) -> Result<bool, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("context".to_string(), self.handle.to_json());
        let result = self.client.invoke_capability("Aspire.Hosting/IUserSecretsManager.isAvailable", args)?;
        Ok(serde_json::from_value(result)?)
    }

    /// Gets the FilePath property
    pub fn file_path(&self) -> Result<String, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("context".to_string(), self.handle.to_json());
        let result = self.client.invoke_capability("Aspire.Hosting/IUserSecretsManager.filePath", args)?;
        Ok(serde_json::from_value(result)?)
    }

    /// Attempts to set a user secret value
    pub fn try_set_secret(&self, name: &str, value: &str) -> Result<bool, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("context".to_string(), self.handle.to_json());
        args.insert("name".to_string(), serde_json::to_value(&name).unwrap_or(Value::Null));
        args.insert("value".to_string(), serde_json::to_value(&value).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting/IUserSecretsManager.trySetSecret", args)?;
        Ok(serde_json::from_value(result)?)
    }

    /// Saves state to user secrets from a JSON string
    pub fn save_state_json(&self, json: &str, cancellation_token: Option<&CancellationToken>) -> Result<(), Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("userSecretsManager".to_string(), self.handle.to_json());
        args.insert("json".to_string(), serde_json::to_value(&json).unwrap_or(Value::Null));
        if let Some(token) = cancellation_token {
            let token_id = register_cancellation(token, self.client.clone());
            args.insert("cancellationToken".to_string(), Value::String(token_id));
        }
        let result = self.client.invoke_capability("Aspire.Hosting/saveStateJson", args)?;
        Ok(())
    }

    /// Gets a secret value if it exists, or sets it to the provided value if it does not
    pub fn get_or_set_secret(&self, resource_builder: &IResource, name: &str, value: &str) -> Result<(), Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("userSecretsManager".to_string(), self.handle.to_json());
        args.insert("resourceBuilder".to_string(), resource_builder.handle().to_json());
        args.insert("name".to_string(), serde_json::to_value(&name).unwrap_or(Value::Null));
        args.insert("value".to_string(), serde_json::to_value(&value).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting/getOrSetSecret", args)?;
        Ok(())
    }
}

/// Wrapper for Aspire.Hosting/Aspire.Hosting.ApplicationModel.InitializeResourceEvent
pub struct InitializeResourceEvent {
    handle: Handle,
    client: Arc<AspireClient>,
}

impl HasHandle for InitializeResourceEvent {
    fn handle(&self) -> &Handle {
        &self.handle
    }
}

impl InitializeResourceEvent {
    pub fn new(handle: Handle, client: Arc<AspireClient>) -> Self {
        Self { handle, client }
    }

    pub fn handle(&self) -> &Handle {
        &self.handle
    }

    pub fn client(&self) -> &Arc<AspireClient> {
        &self.client
    }

    /// Gets the Resource property
    pub fn resource(&self) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("context".to_string(), self.handle.to_json());
        let result = self.client.invoke_capability("Aspire.Hosting.ApplicationModel/InitializeResourceEvent.resource", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Gets the Eventing property
    pub fn eventing(&self) -> Result<IDistributedApplicationEventing, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("context".to_string(), self.handle.to_json());
        let result = self.client.invoke_capability("Aspire.Hosting.ApplicationModel/InitializeResourceEvent.eventing", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IDistributedApplicationEventing::new(handle, self.client.clone()))
    }

    /// Gets the Logger property
    pub fn logger(&self) -> Result<ILogger, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("context".to_string(), self.handle.to_json());
        let result = self.client.invoke_capability("Aspire.Hosting.ApplicationModel/InitializeResourceEvent.logger", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(ILogger::new(handle, self.client.clone()))
    }

    /// Gets the Notifications property
    pub fn notifications(&self) -> Result<ResourceNotificationService, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("context".to_string(), self.handle.to_json());
        let result = self.client.invoke_capability("Aspire.Hosting.ApplicationModel/InitializeResourceEvent.notifications", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(ResourceNotificationService::new(handle, self.client.clone()))
    }

    /// Gets the Services property
    pub fn services(&self) -> Result<IServiceProvider, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("context".to_string(), self.handle.to_json());
        let result = self.client.invoke_capability("Aspire.Hosting.ApplicationModel/InitializeResourceEvent.services", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IServiceProvider::new(handle, self.client.clone()))
    }
}

/// Wrapper for Aspire.Hosting/Aspire.Hosting.ApplicationModel.ParameterResource
pub struct ParameterResource {
    handle: Handle,
    client: Arc<AspireClient>,
}

impl HasHandle for ParameterResource {
    fn handle(&self) -> &Handle {
        &self.handle
    }
}

impl ParameterResource {
    pub fn new(handle: Handle, client: Arc<AspireClient>) -> Self {
        Self { handle, client }
    }

    pub fn handle(&self) -> &Handle {
        &self.handle
    }

    pub fn client(&self) -> &Arc<AspireClient> {
        &self.client
    }

    /// Configures a resource to use a container registry
    pub fn with_container_registry(&self, registry: &IResource) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("registry".to_string(), registry.handle().to_json());
        let result = self.client.invoke_capability("Aspire.Hosting/withContainerRegistry", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Sets the base image for a Dockerfile build
    pub fn with_dockerfile_base_image(&self, build_image: Option<&str>, runtime_image: Option<&str>) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        if let Some(ref v) = build_image {
            args.insert("buildImage".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        if let Some(ref v) = runtime_image {
            args.insert("runtimeImage".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        let result = self.client.invoke_capability("Aspire.Hosting/withDockerfileBaseImage", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Sets a parameter description
    pub fn with_description(&self, description: &str, enable_markdown: Option<bool>) -> Result<ParameterResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("description".to_string(), serde_json::to_value(&description).unwrap_or(Value::Null));
        if let Some(ref v) = enable_markdown {
            args.insert("enableMarkdown".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        let result = self.client.invoke_capability("Aspire.Hosting/withDescription", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(ParameterResource::new(handle, self.client.clone()))
    }

    /// Adds a required command dependency
    pub fn with_required_command(&self, command: &str, help_link: Option<&str>) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("command".to_string(), serde_json::to_value(&command).unwrap_or(Value::Null));
        if let Some(ref v) = help_link {
            args.insert("helpLink".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        let result = self.client.invoke_capability("Aspire.Hosting/withRequiredCommand", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Customizes displayed URLs via callback
    pub fn with_urls_callback(&self, callback: impl Fn(Vec<Value>) -> Value + Send + Sync + 'static) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        let callback_id = register_callback(callback);
        args.insert("callback".to_string(), Value::String(callback_id));
        let result = self.client.invoke_capability("Aspire.Hosting/withUrlsCallback", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Adds or modifies displayed URLs
    pub fn with_url(&self, url: &str, display_text: Option<&str>) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("url".to_string(), serde_json::to_value(&url).unwrap_or(Value::Null));
        if let Some(ref v) = display_text {
            args.insert("displayText".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        let result = self.client.invoke_capability("Aspire.Hosting/withUrl", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Adds a URL using a reference expression
    pub fn with_url_expression(&self, url: ReferenceExpression, display_text: Option<&str>) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("url".to_string(), serde_json::to_value(&url).unwrap_or(Value::Null));
        if let Some(ref v) = display_text {
            args.insert("displayText".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        let result = self.client.invoke_capability("Aspire.Hosting/withUrlExpression", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Customizes the URL for a specific endpoint via callback
    pub fn with_url_for_endpoint(&self, endpoint_name: &str, callback: impl Fn(Vec<Value>) -> Value + Send + Sync + 'static) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("endpointName".to_string(), serde_json::to_value(&endpoint_name).unwrap_or(Value::Null));
        let callback_id = register_callback(callback);
        args.insert("callback".to_string(), Value::String(callback_id));
        let result = self.client.invoke_capability("Aspire.Hosting/withUrlForEndpoint", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Excludes the resource from the deployment manifest
    pub fn exclude_from_manifest(&self) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        let result = self.client.invoke_capability("Aspire.Hosting/excludeFromManifest", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Prevents resource from starting automatically
    pub fn with_explicit_start(&self) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        let result = self.client.invoke_capability("Aspire.Hosting/withExplicitStart", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Adds a health check by key
    pub fn with_health_check(&self, key: &str) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("key".to_string(), serde_json::to_value(&key).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting/withHealthCheck", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Adds a resource command
    pub fn with_command(&self, name: &str, display_name: &str, execute_command: impl Fn(Vec<Value>) -> Value + Send + Sync + 'static, command_options: Option<CommandOptions>) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("name".to_string(), serde_json::to_value(&name).unwrap_or(Value::Null));
        args.insert("displayName".to_string(), serde_json::to_value(&display_name).unwrap_or(Value::Null));
        let callback_id = register_callback(execute_command);
        args.insert("executeCommand".to_string(), Value::String(callback_id));
        if let Some(ref v) = command_options {
            args.insert("commandOptions".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        let result = self.client.invoke_capability("Aspire.Hosting/withCommand", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Sets the parent relationship
    pub fn with_parent_relationship(&self, parent: &IResource) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("parent".to_string(), parent.handle().to_json());
        let result = self.client.invoke_capability("Aspire.Hosting/withBuilderParentRelationship", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Sets a child relationship
    pub fn with_child_relationship(&self, child: &IResource) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("child".to_string(), child.handle().to_json());
        let result = self.client.invoke_capability("Aspire.Hosting/withBuilderChildRelationship", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Sets the icon for the resource
    pub fn with_icon_name(&self, icon_name: &str, icon_variant: Option<IconVariant>) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("iconName".to_string(), serde_json::to_value(&icon_name).unwrap_or(Value::Null));
        if let Some(ref v) = icon_variant {
            args.insert("iconVariant".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        let result = self.client.invoke_capability("Aspire.Hosting/withIconName", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Excludes the resource from MCP server exposure
    pub fn exclude_from_mcp(&self) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        let result = self.client.invoke_capability("Aspire.Hosting/excludeFromMcp", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Adds a pipeline step to the resource
    pub fn with_pipeline_step_factory(&self, step_name: &str, callback: impl Fn(Vec<Value>) -> Value + Send + Sync + 'static, depends_on: Option<Vec<String>>, required_by: Option<Vec<String>>, tags: Option<Vec<String>>, description: Option<&str>) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("stepName".to_string(), serde_json::to_value(&step_name).unwrap_or(Value::Null));
        let callback_id = register_callback(callback);
        args.insert("callback".to_string(), Value::String(callback_id));
        if let Some(ref v) = depends_on {
            args.insert("dependsOn".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        if let Some(ref v) = required_by {
            args.insert("requiredBy".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        if let Some(ref v) = tags {
            args.insert("tags".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        if let Some(ref v) = description {
            args.insert("description".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        let result = self.client.invoke_capability("Aspire.Hosting/withPipelineStepFactory", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Configures pipeline step dependencies via a callback
    pub fn with_pipeline_configuration(&self, callback: impl Fn(Vec<Value>) -> Value + Send + Sync + 'static) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        let callback_id = register_callback(callback);
        args.insert("callback".to_string(), Value::String(callback_id));
        let result = self.client.invoke_capability("Aspire.Hosting/withPipelineConfiguration", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Gets the resource name
    pub fn get_resource_name(&self) -> Result<String, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("resource".to_string(), self.handle.to_json());
        let result = self.client.invoke_capability("Aspire.Hosting/getResourceName", args)?;
        Ok(serde_json::from_value(result)?)
    }

    /// Subscribes to the BeforeResourceStarted event
    pub fn on_before_resource_started(&self, callback: impl Fn(Vec<Value>) -> Value + Send + Sync + 'static) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        let callback_id = register_callback(callback);
        args.insert("callback".to_string(), Value::String(callback_id));
        let result = self.client.invoke_capability("Aspire.Hosting/onBeforeResourceStarted", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Subscribes to the ResourceStopped event
    pub fn on_resource_stopped(&self, callback: impl Fn(Vec<Value>) -> Value + Send + Sync + 'static) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        let callback_id = register_callback(callback);
        args.insert("callback".to_string(), Value::String(callback_id));
        let result = self.client.invoke_capability("Aspire.Hosting/onResourceStopped", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Subscribes to the InitializeResource event
    pub fn on_initialize_resource(&self, callback: impl Fn(Vec<Value>) -> Value + Send + Sync + 'static) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        let callback_id = register_callback(callback);
        args.insert("callback".to_string(), Value::String(callback_id));
        let result = self.client.invoke_capability("Aspire.Hosting/onInitializeResource", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Subscribes to the ResourceReady event
    pub fn on_resource_ready(&self, callback: impl Fn(Vec<Value>) -> Value + Send + Sync + 'static) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        let callback_id = register_callback(callback);
        args.insert("callback".to_string(), Value::String(callback_id));
        let result = self.client.invoke_capability("Aspire.Hosting/onResourceReady", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
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

    /// Adds a dependency from a string or another resource
    pub fn with_union_dependency(&self, dependency: Value) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("dependency".to_string(), serde_json::to_value(&dependency).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting.CodeGeneration.Rust.Tests/withUnionDependency", args)?;
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

    /// Adds a label to the resource
    pub fn with_merge_label(&self, label: &str) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("label".to_string(), serde_json::to_value(&label).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting.CodeGeneration.Rust.Tests/withMergeLabel", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Adds a categorized label to the resource
    pub fn with_merge_label_categorized(&self, label: &str, category: &str) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("label".to_string(), serde_json::to_value(&label).unwrap_or(Value::Null));
        args.insert("category".to_string(), serde_json::to_value(&category).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting.CodeGeneration.Rust.Tests/withMergeLabelCategorized", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Configures a named endpoint
    pub fn with_merge_endpoint(&self, endpoint_name: &str, port: f64) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("endpointName".to_string(), serde_json::to_value(&endpoint_name).unwrap_or(Value::Null));
        args.insert("port".to_string(), serde_json::to_value(&port).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting.CodeGeneration.Rust.Tests/withMergeEndpoint", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Configures a named endpoint with scheme
    pub fn with_merge_endpoint_scheme(&self, endpoint_name: &str, port: f64, scheme: &str) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("endpointName".to_string(), serde_json::to_value(&endpoint_name).unwrap_or(Value::Null));
        args.insert("port".to_string(), serde_json::to_value(&port).unwrap_or(Value::Null));
        args.insert("scheme".to_string(), serde_json::to_value(&scheme).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting.CodeGeneration.Rust.Tests/withMergeEndpointScheme", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Configures resource logging
    pub fn with_merge_logging(&self, log_level: &str, enable_console: Option<bool>, max_files: Option<f64>) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("logLevel".to_string(), serde_json::to_value(&log_level).unwrap_or(Value::Null));
        if let Some(ref v) = enable_console {
            args.insert("enableConsole".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        if let Some(ref v) = max_files {
            args.insert("maxFiles".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        let result = self.client.invoke_capability("Aspire.Hosting.CodeGeneration.Rust.Tests/withMergeLogging", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Configures resource logging with file path
    pub fn with_merge_logging_path(&self, log_level: &str, log_path: &str, enable_console: Option<bool>, max_files: Option<f64>) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("logLevel".to_string(), serde_json::to_value(&log_level).unwrap_or(Value::Null));
        args.insert("logPath".to_string(), serde_json::to_value(&log_path).unwrap_or(Value::Null));
        if let Some(ref v) = enable_console {
            args.insert("enableConsole".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        if let Some(ref v) = max_files {
            args.insert("maxFiles".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        let result = self.client.invoke_capability("Aspire.Hosting.CodeGeneration.Rust.Tests/withMergeLoggingPath", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Configures a route
    pub fn with_merge_route(&self, path: &str, method: &str, handler: &str, priority: f64) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("path".to_string(), serde_json::to_value(&path).unwrap_or(Value::Null));
        args.insert("method".to_string(), serde_json::to_value(&method).unwrap_or(Value::Null));
        args.insert("handler".to_string(), serde_json::to_value(&handler).unwrap_or(Value::Null));
        args.insert("priority".to_string(), serde_json::to_value(&priority).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting.CodeGeneration.Rust.Tests/withMergeRoute", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Configures a route with middleware
    pub fn with_merge_route_middleware(&self, path: &str, method: &str, handler: &str, priority: f64, middleware: &str) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("path".to_string(), serde_json::to_value(&path).unwrap_or(Value::Null));
        args.insert("method".to_string(), serde_json::to_value(&method).unwrap_or(Value::Null));
        args.insert("handler".to_string(), serde_json::to_value(&handler).unwrap_or(Value::Null));
        args.insert("priority".to_string(), serde_json::to_value(&priority).unwrap_or(Value::Null));
        args.insert("middleware".to_string(), serde_json::to_value(&middleware).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting.CodeGeneration.Rust.Tests/withMergeRouteMiddleware", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }
}

/// Wrapper for Aspire.Hosting/Aspire.Hosting.Pipelines.PipelineConfigurationContext
pub struct PipelineConfigurationContext {
    handle: Handle,
    client: Arc<AspireClient>,
}

impl HasHandle for PipelineConfigurationContext {
    fn handle(&self) -> &Handle {
        &self.handle
    }
}

impl PipelineConfigurationContext {
    pub fn new(handle: Handle, client: Arc<AspireClient>) -> Self {
        Self { handle, client }
    }

    pub fn handle(&self) -> &Handle {
        &self.handle
    }

    pub fn client(&self) -> &Arc<AspireClient> {
        &self.client
    }

    /// Gets the Services property
    pub fn services(&self) -> Result<IServiceProvider, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("context".to_string(), self.handle.to_json());
        let result = self.client.invoke_capability("Aspire.Hosting.Pipelines/PipelineConfigurationContext.services", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IServiceProvider::new(handle, self.client.clone()))
    }

    /// Sets the Services property
    pub fn set_services(&self, value: &IServiceProvider) -> Result<PipelineConfigurationContext, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("context".to_string(), self.handle.to_json());
        args.insert("value".to_string(), value.handle().to_json());
        let result = self.client.invoke_capability("Aspire.Hosting.Pipelines/PipelineConfigurationContext.setServices", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(PipelineConfigurationContext::new(handle, self.client.clone()))
    }

    /// Gets the Steps property
    pub fn steps(&self) -> Result<Vec<PipelineStep>, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("context".to_string(), self.handle.to_json());
        let result = self.client.invoke_capability("Aspire.Hosting.Pipelines/PipelineConfigurationContext.steps", args)?;
        let handles: Vec<Handle> = serde_json::from_value(result)?;
        Ok(handles.into_iter().map(|h| PipelineStep::new(h, self.client.clone())).collect())
    }

    /// Sets the Steps property
    pub fn set_steps(&self, value: Vec<PipelineStep>) -> Result<PipelineConfigurationContext, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("context".to_string(), self.handle.to_json());
        let handles: Vec<Value> = value.iter().map(|item| item.handle().to_json()).collect();
        args.insert("value".to_string(), Value::Array(handles));
        let result = self.client.invoke_capability("Aspire.Hosting.Pipelines/PipelineConfigurationContext.setSteps", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(PipelineConfigurationContext::new(handle, self.client.clone()))
    }

    /// Gets the Model property
    pub fn model(&self) -> Result<DistributedApplicationModel, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("context".to_string(), self.handle.to_json());
        let result = self.client.invoke_capability("Aspire.Hosting.Pipelines/PipelineConfigurationContext.model", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(DistributedApplicationModel::new(handle, self.client.clone()))
    }

    /// Sets the Model property
    pub fn set_model(&self, value: &DistributedApplicationModel) -> Result<PipelineConfigurationContext, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("context".to_string(), self.handle.to_json());
        args.insert("value".to_string(), value.handle().to_json());
        let result = self.client.invoke_capability("Aspire.Hosting.Pipelines/PipelineConfigurationContext.setModel", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(PipelineConfigurationContext::new(handle, self.client.clone()))
    }

    /// Gets pipeline steps with the specified tag
    pub fn get_steps_by_tag(&self, tag: &str) -> Result<Vec<PipelineStep>, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("context".to_string(), self.handle.to_json());
        args.insert("tag".to_string(), serde_json::to_value(&tag).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting.Pipelines/getStepsByTag", args)?;
        let handles: Vec<Handle> = serde_json::from_value(result)?;
        Ok(handles.into_iter().map(|h| PipelineStep::new(h, self.client.clone())).collect())
    }
}

/// Wrapper for Aspire.Hosting/Aspire.Hosting.Pipelines.PipelineContext
pub struct PipelineContext {
    handle: Handle,
    client: Arc<AspireClient>,
}

impl HasHandle for PipelineContext {
    fn handle(&self) -> &Handle {
        &self.handle
    }
}

impl PipelineContext {
    pub fn new(handle: Handle, client: Arc<AspireClient>) -> Self {
        Self { handle, client }
    }

    pub fn handle(&self) -> &Handle {
        &self.handle
    }

    pub fn client(&self) -> &Arc<AspireClient> {
        &self.client
    }

    /// Gets the Model property
    pub fn model(&self) -> Result<DistributedApplicationModel, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("context".to_string(), self.handle.to_json());
        let result = self.client.invoke_capability("Aspire.Hosting.Pipelines/PipelineContext.model", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(DistributedApplicationModel::new(handle, self.client.clone()))
    }

    /// Gets the ExecutionContext property
    pub fn execution_context(&self) -> Result<DistributedApplicationExecutionContext, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("context".to_string(), self.handle.to_json());
        let result = self.client.invoke_capability("Aspire.Hosting.Pipelines/PipelineContext.executionContext", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(DistributedApplicationExecutionContext::new(handle, self.client.clone()))
    }

    /// Gets the Services property
    pub fn services(&self) -> Result<IServiceProvider, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("context".to_string(), self.handle.to_json());
        let result = self.client.invoke_capability("Aspire.Hosting.Pipelines/PipelineContext.services", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IServiceProvider::new(handle, self.client.clone()))
    }

    /// Gets the Logger property
    pub fn logger(&self) -> Result<ILogger, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("context".to_string(), self.handle.to_json());
        let result = self.client.invoke_capability("Aspire.Hosting.Pipelines/PipelineContext.logger", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(ILogger::new(handle, self.client.clone()))
    }

    /// Gets the CancellationToken property
    pub fn cancellation_token(&self) -> Result<CancellationToken, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("context".to_string(), self.handle.to_json());
        let result = self.client.invoke_capability("Aspire.Hosting.Pipelines/PipelineContext.cancellationToken", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(CancellationToken::new(handle, self.client.clone()))
    }

    /// Sets the CancellationToken property
    pub fn set_cancellation_token(&self, value: Option<&CancellationToken>) -> Result<PipelineContext, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("context".to_string(), self.handle.to_json());
        if let Some(token) = value {
            let token_id = register_cancellation(token, self.client.clone());
            args.insert("value".to_string(), Value::String(token_id));
        }
        let result = self.client.invoke_capability("Aspire.Hosting.Pipelines/PipelineContext.setCancellationToken", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(PipelineContext::new(handle, self.client.clone()))
    }

    /// Gets the Summary property
    pub fn summary(&self) -> Result<PipelineSummary, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("context".to_string(), self.handle.to_json());
        let result = self.client.invoke_capability("Aspire.Hosting.Pipelines/PipelineContext.summary", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(PipelineSummary::new(handle, self.client.clone()))
    }
}

/// Wrapper for Aspire.Hosting/Aspire.Hosting.Pipelines.PipelineStep
pub struct PipelineStep {
    handle: Handle,
    client: Arc<AspireClient>,
}

impl HasHandle for PipelineStep {
    fn handle(&self) -> &Handle {
        &self.handle
    }
}

impl PipelineStep {
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
        let result = self.client.invoke_capability("Aspire.Hosting.Pipelines/PipelineStep.name", args)?;
        Ok(serde_json::from_value(result)?)
    }

    /// Sets the Name property
    pub fn set_name(&self, value: &str) -> Result<PipelineStep, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("context".to_string(), self.handle.to_json());
        args.insert("value".to_string(), serde_json::to_value(&value).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting.Pipelines/PipelineStep.setName", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(PipelineStep::new(handle, self.client.clone()))
    }

    /// Gets the Description property
    pub fn description(&self) -> Result<String, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("context".to_string(), self.handle.to_json());
        let result = self.client.invoke_capability("Aspire.Hosting.Pipelines/PipelineStep.description", args)?;
        Ok(serde_json::from_value(result)?)
    }

    /// Sets the Description property
    pub fn set_description(&self, value: &str) -> Result<PipelineStep, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("context".to_string(), self.handle.to_json());
        args.insert("value".to_string(), serde_json::to_value(&value).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting.Pipelines/PipelineStep.setDescription", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(PipelineStep::new(handle, self.client.clone()))
    }

    /// Gets the DependsOnSteps property
    pub fn depends_on_steps(&self) -> AspireList<String> {
        AspireList::with_getter(self.handle.clone(), self.client.clone(), "Aspire.Hosting.Pipelines/PipelineStep.dependsOnSteps")
    }

    /// Sets the DependsOnSteps property
    pub fn set_depends_on_steps(&self, value: AspireList<String>) -> Result<PipelineStep, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("context".to_string(), self.handle.to_json());
        args.insert("value".to_string(), value.handle().to_json());
        let result = self.client.invoke_capability("Aspire.Hosting.Pipelines/PipelineStep.setDependsOnSteps", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(PipelineStep::new(handle, self.client.clone()))
    }

    /// Gets the RequiredBySteps property
    pub fn required_by_steps(&self) -> AspireList<String> {
        AspireList::with_getter(self.handle.clone(), self.client.clone(), "Aspire.Hosting.Pipelines/PipelineStep.requiredBySteps")
    }

    /// Sets the RequiredBySteps property
    pub fn set_required_by_steps(&self, value: AspireList<String>) -> Result<PipelineStep, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("context".to_string(), self.handle.to_json());
        args.insert("value".to_string(), value.handle().to_json());
        let result = self.client.invoke_capability("Aspire.Hosting.Pipelines/PipelineStep.setRequiredBySteps", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(PipelineStep::new(handle, self.client.clone()))
    }

    /// Gets the Tags property
    pub fn tags(&self) -> AspireList<String> {
        AspireList::with_getter(self.handle.clone(), self.client.clone(), "Aspire.Hosting.Pipelines/PipelineStep.tags")
    }

    /// Sets the Tags property
    pub fn set_tags(&self, value: AspireList<String>) -> Result<PipelineStep, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("context".to_string(), self.handle.to_json());
        args.insert("value".to_string(), value.handle().to_json());
        let result = self.client.invoke_capability("Aspire.Hosting.Pipelines/PipelineStep.setTags", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(PipelineStep::new(handle, self.client.clone()))
    }

    /// Gets the Resource property
    pub fn resource(&self) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("context".to_string(), self.handle.to_json());
        let result = self.client.invoke_capability("Aspire.Hosting.Pipelines/PipelineStep.resource", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Sets the Resource property
    pub fn set_resource(&self, value: &IResource) -> Result<PipelineStep, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("context".to_string(), self.handle.to_json());
        args.insert("value".to_string(), value.handle().to_json());
        let result = self.client.invoke_capability("Aspire.Hosting.Pipelines/PipelineStep.setResource", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(PipelineStep::new(handle, self.client.clone()))
    }

    /// Adds a dependency on another step by name
    pub fn depends_on(&self, step_name: &str) -> Result<(), Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("context".to_string(), self.handle.to_json());
        args.insert("stepName".to_string(), serde_json::to_value(&step_name).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting.Pipelines/dependsOn", args)?;
        Ok(())
    }

    /// Specifies that another step requires this step by name
    pub fn required_by(&self, step_name: &str) -> Result<(), Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("context".to_string(), self.handle.to_json());
        args.insert("stepName".to_string(), serde_json::to_value(&step_name).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting.Pipelines/requiredBy", args)?;
        Ok(())
    }
}

/// Wrapper for Aspire.Hosting/Aspire.Hosting.Pipelines.PipelineStepContext
pub struct PipelineStepContext {
    handle: Handle,
    client: Arc<AspireClient>,
}

impl HasHandle for PipelineStepContext {
    fn handle(&self) -> &Handle {
        &self.handle
    }
}

impl PipelineStepContext {
    pub fn new(handle: Handle, client: Arc<AspireClient>) -> Self {
        Self { handle, client }
    }

    pub fn handle(&self) -> &Handle {
        &self.handle
    }

    pub fn client(&self) -> &Arc<AspireClient> {
        &self.client
    }

    /// Gets the PipelineContext property
    pub fn pipeline_context(&self) -> Result<PipelineContext, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("context".to_string(), self.handle.to_json());
        let result = self.client.invoke_capability("Aspire.Hosting.Pipelines/PipelineStepContext.pipelineContext", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(PipelineContext::new(handle, self.client.clone()))
    }

    /// Sets the PipelineContext property
    pub fn set_pipeline_context(&self, value: &PipelineContext) -> Result<PipelineStepContext, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("context".to_string(), self.handle.to_json());
        args.insert("value".to_string(), value.handle().to_json());
        let result = self.client.invoke_capability("Aspire.Hosting.Pipelines/PipelineStepContext.setPipelineContext", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(PipelineStepContext::new(handle, self.client.clone()))
    }

    /// Gets the ReportingStep property
    pub fn reporting_step(&self) -> Result<IReportingStep, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("context".to_string(), self.handle.to_json());
        let result = self.client.invoke_capability("Aspire.Hosting.Pipelines/PipelineStepContext.reportingStep", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IReportingStep::new(handle, self.client.clone()))
    }

    /// Sets the ReportingStep property
    pub fn set_reporting_step(&self, value: &IReportingStep) -> Result<PipelineStepContext, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("context".to_string(), self.handle.to_json());
        args.insert("value".to_string(), value.handle().to_json());
        let result = self.client.invoke_capability("Aspire.Hosting.Pipelines/PipelineStepContext.setReportingStep", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(PipelineStepContext::new(handle, self.client.clone()))
    }

    /// Gets the Model property
    pub fn model(&self) -> Result<DistributedApplicationModel, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("context".to_string(), self.handle.to_json());
        let result = self.client.invoke_capability("Aspire.Hosting.Pipelines/PipelineStepContext.model", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(DistributedApplicationModel::new(handle, self.client.clone()))
    }

    /// Gets the ExecutionContext property
    pub fn execution_context(&self) -> Result<DistributedApplicationExecutionContext, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("context".to_string(), self.handle.to_json());
        let result = self.client.invoke_capability("Aspire.Hosting.Pipelines/PipelineStepContext.executionContext", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(DistributedApplicationExecutionContext::new(handle, self.client.clone()))
    }

    /// Gets the Services property
    pub fn services(&self) -> Result<IServiceProvider, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("context".to_string(), self.handle.to_json());
        let result = self.client.invoke_capability("Aspire.Hosting.Pipelines/PipelineStepContext.services", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IServiceProvider::new(handle, self.client.clone()))
    }

    /// Gets the Logger property
    pub fn logger(&self) -> Result<ILogger, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("context".to_string(), self.handle.to_json());
        let result = self.client.invoke_capability("Aspire.Hosting.Pipelines/PipelineStepContext.logger", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(ILogger::new(handle, self.client.clone()))
    }

    /// Gets the CancellationToken property
    pub fn cancellation_token(&self) -> Result<CancellationToken, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("context".to_string(), self.handle.to_json());
        let result = self.client.invoke_capability("Aspire.Hosting.Pipelines/PipelineStepContext.cancellationToken", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(CancellationToken::new(handle, self.client.clone()))
    }

    /// Gets the Summary property
    pub fn summary(&self) -> Result<PipelineSummary, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("context".to_string(), self.handle.to_json());
        let result = self.client.invoke_capability("Aspire.Hosting.Pipelines/PipelineStepContext.summary", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(PipelineSummary::new(handle, self.client.clone()))
    }
}

/// Wrapper for Aspire.Hosting/Aspire.Hosting.Pipelines.PipelineStepFactoryContext
pub struct PipelineStepFactoryContext {
    handle: Handle,
    client: Arc<AspireClient>,
}

impl HasHandle for PipelineStepFactoryContext {
    fn handle(&self) -> &Handle {
        &self.handle
    }
}

impl PipelineStepFactoryContext {
    pub fn new(handle: Handle, client: Arc<AspireClient>) -> Self {
        Self { handle, client }
    }

    pub fn handle(&self) -> &Handle {
        &self.handle
    }

    pub fn client(&self) -> &Arc<AspireClient> {
        &self.client
    }

    /// Gets the PipelineContext property
    pub fn pipeline_context(&self) -> Result<PipelineContext, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("context".to_string(), self.handle.to_json());
        let result = self.client.invoke_capability("Aspire.Hosting.Pipelines/PipelineStepFactoryContext.pipelineContext", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(PipelineContext::new(handle, self.client.clone()))
    }

    /// Sets the PipelineContext property
    pub fn set_pipeline_context(&self, value: &PipelineContext) -> Result<PipelineStepFactoryContext, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("context".to_string(), self.handle.to_json());
        args.insert("value".to_string(), value.handle().to_json());
        let result = self.client.invoke_capability("Aspire.Hosting.Pipelines/PipelineStepFactoryContext.setPipelineContext", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(PipelineStepFactoryContext::new(handle, self.client.clone()))
    }

    /// Gets the Resource property
    pub fn resource(&self) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("context".to_string(), self.handle.to_json());
        let result = self.client.invoke_capability("Aspire.Hosting.Pipelines/PipelineStepFactoryContext.resource", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Sets the Resource property
    pub fn set_resource(&self, value: &IResource) -> Result<PipelineStepFactoryContext, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("context".to_string(), self.handle.to_json());
        args.insert("value".to_string(), value.handle().to_json());
        let result = self.client.invoke_capability("Aspire.Hosting.Pipelines/PipelineStepFactoryContext.setResource", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(PipelineStepFactoryContext::new(handle, self.client.clone()))
    }
}

/// Wrapper for Aspire.Hosting/Aspire.Hosting.Pipelines.PipelineSummary
pub struct PipelineSummary {
    handle: Handle,
    client: Arc<AspireClient>,
}

impl HasHandle for PipelineSummary {
    fn handle(&self) -> &Handle {
        &self.handle
    }
}

impl PipelineSummary {
    pub fn new(handle: Handle, client: Arc<AspireClient>) -> Self {
        Self { handle, client }
    }

    pub fn handle(&self) -> &Handle {
        &self.handle
    }

    pub fn client(&self) -> &Arc<AspireClient> {
        &self.client
    }

    /// Invokes the Add method
    pub fn add(&self, key: &str, value: &str) -> Result<(), Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("context".to_string(), self.handle.to_json());
        args.insert("key".to_string(), serde_json::to_value(&key).unwrap_or(Value::Null));
        args.insert("value".to_string(), serde_json::to_value(&value).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting.Pipelines/PipelineSummary.add", args)?;
        Ok(())
    }

    /// Adds a Markdown-formatted value to the pipeline summary
    pub fn add_markdown(&self, key: &str, markdown_string: &str) -> Result<(), Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("summary".to_string(), self.handle.to_json());
        args.insert("key".to_string(), serde_json::to_value(&key).unwrap_or(Value::Null));
        args.insert("markdownString".to_string(), serde_json::to_value(&markdown_string).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting/addMarkdown", args)?;
        Ok(())
    }
}

/// Wrapper for Aspire.Hosting/Aspire.Hosting.ApplicationModel.ProjectResource
pub struct ProjectResource {
    handle: Handle,
    client: Arc<AspireClient>,
}

impl HasHandle for ProjectResource {
    fn handle(&self) -> &Handle {
        &self.handle
    }
}

impl ProjectResource {
    pub fn new(handle: Handle, client: Arc<AspireClient>) -> Self {
        Self { handle, client }
    }

    pub fn handle(&self) -> &Handle {
        &self.handle
    }

    pub fn client(&self) -> &Arc<AspireClient> {
        &self.client
    }

    /// Configures a resource to use a container registry
    pub fn with_container_registry(&self, registry: &IResource) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("registry".to_string(), registry.handle().to_json());
        let result = self.client.invoke_capability("Aspire.Hosting/withContainerRegistry", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Sets the base image for a Dockerfile build
    pub fn with_dockerfile_base_image(&self, build_image: Option<&str>, runtime_image: Option<&str>) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        if let Some(ref v) = build_image {
            args.insert("buildImage".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        if let Some(ref v) = runtime_image {
            args.insert("runtimeImage".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        let result = self.client.invoke_capability("Aspire.Hosting/withDockerfileBaseImage", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Configures an MCP server endpoint on the resource
    pub fn with_mcp_server(&self, path: Option<&str>, endpoint_name: Option<&str>) -> Result<IResourceWithEndpoints, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        if let Some(ref v) = path {
            args.insert("path".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        if let Some(ref v) = endpoint_name {
            args.insert("endpointName".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        let result = self.client.invoke_capability("Aspire.Hosting/withMcpServer", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResourceWithEndpoints::new(handle, self.client.clone()))
    }

    /// Configures OTLP telemetry export
    pub fn with_otlp_exporter(&self) -> Result<IResourceWithEnvironment, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        let result = self.client.invoke_capability("Aspire.Hosting/withOtlpExporter", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResourceWithEnvironment::new(handle, self.client.clone()))
    }

    /// Configures OTLP telemetry export with specific protocol
    pub fn with_otlp_exporter_protocol(&self, protocol: OtlpProtocol) -> Result<IResourceWithEnvironment, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("protocol".to_string(), serde_json::to_value(&protocol).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting/withOtlpExporterProtocol", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResourceWithEnvironment::new(handle, self.client.clone()))
    }

    /// Sets the number of replicas
    pub fn with_replicas(&self, replicas: f64) -> Result<ProjectResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("replicas".to_string(), serde_json::to_value(&replicas).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting/withReplicas", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(ProjectResource::new(handle, self.client.clone()))
    }

    /// Disables forwarded headers for the project
    pub fn disable_forwarded_headers(&self) -> Result<ProjectResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        let result = self.client.invoke_capability("Aspire.Hosting/disableForwardedHeaders", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(ProjectResource::new(handle, self.client.clone()))
    }

    /// Publishes a project as a Docker file with optional container configuration
    pub fn publish_as_docker_file(&self, configure: impl Fn(Vec<Value>) -> Value + Send + Sync + 'static) -> Result<ProjectResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        let callback_id = register_callback(configure);
        args.insert("configure".to_string(), Value::String(callback_id));
        let result = self.client.invoke_capability("Aspire.Hosting/publishProjectAsDockerFileWithConfigure", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(ProjectResource::new(handle, self.client.clone()))
    }

    /// Adds a required command dependency
    pub fn with_required_command(&self, command: &str, help_link: Option<&str>) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("command".to_string(), serde_json::to_value(&command).unwrap_or(Value::Null));
        if let Some(ref v) = help_link {
            args.insert("helpLink".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        let result = self.client.invoke_capability("Aspire.Hosting/withRequiredCommand", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Sets an environment variable
    pub fn with_environment(&self, name: &str, value: Value) -> Result<IResourceWithEnvironment, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("name".to_string(), serde_json::to_value(&name).unwrap_or(Value::Null));
        args.insert("value".to_string(), serde_json::to_value(&value).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting/withEnvironment", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResourceWithEnvironment::new(handle, self.client.clone()))
    }

    /// Sets an environment variable from a reference expression
    pub fn with_environment_expression(&self, name: &str, value: ReferenceExpression) -> Result<IResourceWithEnvironment, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("name".to_string(), serde_json::to_value(&name).unwrap_or(Value::Null));
        args.insert("value".to_string(), serde_json::to_value(&value).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting/withEnvironmentExpression", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResourceWithEnvironment::new(handle, self.client.clone()))
    }

    /// Sets environment variables via callback
    pub fn with_environment_callback(&self, callback: impl Fn(Vec<Value>) -> Value + Send + Sync + 'static) -> Result<IResourceWithEnvironment, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        let callback_id = register_callback(callback);
        args.insert("callback".to_string(), Value::String(callback_id));
        let result = self.client.invoke_capability("Aspire.Hosting/withEnvironmentCallback", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResourceWithEnvironment::new(handle, self.client.clone()))
    }

    /// Sets an environment variable from an endpoint reference
    pub fn with_environment_endpoint(&self, name: &str, endpoint_reference: &EndpointReference) -> Result<IResourceWithEnvironment, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("name".to_string(), serde_json::to_value(&name).unwrap_or(Value::Null));
        args.insert("endpointReference".to_string(), endpoint_reference.handle().to_json());
        let result = self.client.invoke_capability("Aspire.Hosting/withEnvironmentEndpoint", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResourceWithEnvironment::new(handle, self.client.clone()))
    }

    /// Sets an environment variable from a parameter resource
    pub fn with_environment_parameter(&self, name: &str, parameter: &ParameterResource) -> Result<IResourceWithEnvironment, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("name".to_string(), serde_json::to_value(&name).unwrap_or(Value::Null));
        args.insert("parameter".to_string(), parameter.handle().to_json());
        let result = self.client.invoke_capability("Aspire.Hosting/withEnvironmentParameter", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResourceWithEnvironment::new(handle, self.client.clone()))
    }

    /// Sets an environment variable from a connection string resource
    pub fn with_environment_connection_string(&self, env_var_name: &str, resource: &IResourceWithConnectionString) -> Result<IResourceWithEnvironment, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("envVarName".to_string(), serde_json::to_value(&env_var_name).unwrap_or(Value::Null));
        args.insert("resource".to_string(), resource.handle().to_json());
        let result = self.client.invoke_capability("Aspire.Hosting/withEnvironmentConnectionString", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResourceWithEnvironment::new(handle, self.client.clone()))
    }

    /// Adds arguments
    pub fn with_args(&self, args: Vec<String>) -> Result<IResourceWithArgs, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("args".to_string(), serde_json::to_value(&args).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting/withArgs", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResourceWithArgs::new(handle, self.client.clone()))
    }

    /// Sets command-line arguments via callback
    pub fn with_args_callback(&self, callback: impl Fn(Vec<Value>) -> Value + Send + Sync + 'static) -> Result<IResourceWithArgs, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        let callback_id = register_callback(callback);
        args.insert("callback".to_string(), Value::String(callback_id));
        let result = self.client.invoke_capability("Aspire.Hosting/withArgsCallback", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResourceWithArgs::new(handle, self.client.clone()))
    }

    /// Adds a reference to another resource
    pub fn with_reference(&self, source: &IResource, connection_name: Option<&str>, optional: Option<bool>, name: Option<&str>) -> Result<IResourceWithEnvironment, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("source".to_string(), source.handle().to_json());
        if let Some(ref v) = connection_name {
            args.insert("connectionName".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        if let Some(ref v) = optional {
            args.insert("optional".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        if let Some(ref v) = name {
            args.insert("name".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        let result = self.client.invoke_capability("Aspire.Hosting/withReference", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResourceWithEnvironment::new(handle, self.client.clone()))
    }

    /// Adds a reference to a URI
    pub fn with_reference_uri(&self, name: &str, uri: &str) -> Result<IResourceWithEnvironment, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("name".to_string(), serde_json::to_value(&name).unwrap_or(Value::Null));
        args.insert("uri".to_string(), serde_json::to_value(&uri).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting/withReferenceUri", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResourceWithEnvironment::new(handle, self.client.clone()))
    }

    /// Adds a reference to an external service
    pub fn with_reference_external_service(&self, external_service: &ExternalServiceResource) -> Result<IResourceWithEnvironment, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("externalService".to_string(), external_service.handle().to_json());
        let result = self.client.invoke_capability("Aspire.Hosting/withReferenceExternalService", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResourceWithEnvironment::new(handle, self.client.clone()))
    }

    /// Adds a reference to an endpoint
    pub fn with_reference_endpoint(&self, endpoint_reference: &EndpointReference) -> Result<IResourceWithEnvironment, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("endpointReference".to_string(), endpoint_reference.handle().to_json());
        let result = self.client.invoke_capability("Aspire.Hosting/withReferenceEndpoint", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResourceWithEnvironment::new(handle, self.client.clone()))
    }

    /// Adds a network endpoint
    pub fn with_endpoint(&self, port: Option<f64>, target_port: Option<f64>, scheme: Option<&str>, name: Option<&str>, env: Option<&str>, is_proxied: Option<bool>, is_external: Option<bool>, protocol: Option<ProtocolType>) -> Result<IResourceWithEndpoints, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        if let Some(ref v) = port {
            args.insert("port".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        if let Some(ref v) = target_port {
            args.insert("targetPort".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        if let Some(ref v) = scheme {
            args.insert("scheme".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        if let Some(ref v) = name {
            args.insert("name".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        if let Some(ref v) = env {
            args.insert("env".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        if let Some(ref v) = is_proxied {
            args.insert("isProxied".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        if let Some(ref v) = is_external {
            args.insert("isExternal".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        if let Some(ref v) = protocol {
            args.insert("protocol".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        let result = self.client.invoke_capability("Aspire.Hosting/withEndpoint", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResourceWithEndpoints::new(handle, self.client.clone()))
    }

    /// Adds an HTTP endpoint
    pub fn with_http_endpoint(&self, port: Option<f64>, target_port: Option<f64>, name: Option<&str>, env: Option<&str>, is_proxied: Option<bool>) -> Result<IResourceWithEndpoints, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        if let Some(ref v) = port {
            args.insert("port".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        if let Some(ref v) = target_port {
            args.insert("targetPort".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        if let Some(ref v) = name {
            args.insert("name".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        if let Some(ref v) = env {
            args.insert("env".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        if let Some(ref v) = is_proxied {
            args.insert("isProxied".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        let result = self.client.invoke_capability("Aspire.Hosting/withHttpEndpoint", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResourceWithEndpoints::new(handle, self.client.clone()))
    }

    /// Adds an HTTPS endpoint
    pub fn with_https_endpoint(&self, port: Option<f64>, target_port: Option<f64>, name: Option<&str>, env: Option<&str>, is_proxied: Option<bool>) -> Result<IResourceWithEndpoints, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        if let Some(ref v) = port {
            args.insert("port".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        if let Some(ref v) = target_port {
            args.insert("targetPort".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        if let Some(ref v) = name {
            args.insert("name".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        if let Some(ref v) = env {
            args.insert("env".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        if let Some(ref v) = is_proxied {
            args.insert("isProxied".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        let result = self.client.invoke_capability("Aspire.Hosting/withHttpsEndpoint", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResourceWithEndpoints::new(handle, self.client.clone()))
    }

    /// Makes HTTP endpoints externally accessible
    pub fn with_external_http_endpoints(&self) -> Result<IResourceWithEndpoints, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        let result = self.client.invoke_capability("Aspire.Hosting/withExternalHttpEndpoints", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResourceWithEndpoints::new(handle, self.client.clone()))
    }

    /// Gets an endpoint reference
    pub fn get_endpoint(&self, name: &str) -> Result<EndpointReference, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("name".to_string(), serde_json::to_value(&name).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting/getEndpoint", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(EndpointReference::new(handle, self.client.clone()))
    }

    /// Configures resource for HTTP/2
    pub fn as_http2_service(&self) -> Result<IResourceWithEndpoints, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        let result = self.client.invoke_capability("Aspire.Hosting/asHttp2Service", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResourceWithEndpoints::new(handle, self.client.clone()))
    }

    /// Customizes displayed URLs via callback
    pub fn with_urls_callback(&self, callback: impl Fn(Vec<Value>) -> Value + Send + Sync + 'static) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        let callback_id = register_callback(callback);
        args.insert("callback".to_string(), Value::String(callback_id));
        let result = self.client.invoke_capability("Aspire.Hosting/withUrlsCallback", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Adds or modifies displayed URLs
    pub fn with_url(&self, url: &str, display_text: Option<&str>) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("url".to_string(), serde_json::to_value(&url).unwrap_or(Value::Null));
        if let Some(ref v) = display_text {
            args.insert("displayText".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        let result = self.client.invoke_capability("Aspire.Hosting/withUrl", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Adds a URL using a reference expression
    pub fn with_url_expression(&self, url: ReferenceExpression, display_text: Option<&str>) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("url".to_string(), serde_json::to_value(&url).unwrap_or(Value::Null));
        if let Some(ref v) = display_text {
            args.insert("displayText".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        let result = self.client.invoke_capability("Aspire.Hosting/withUrlExpression", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Customizes the URL for a specific endpoint via callback
    pub fn with_url_for_endpoint(&self, endpoint_name: &str, callback: impl Fn(Vec<Value>) -> Value + Send + Sync + 'static) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("endpointName".to_string(), serde_json::to_value(&endpoint_name).unwrap_or(Value::Null));
        let callback_id = register_callback(callback);
        args.insert("callback".to_string(), Value::String(callback_id));
        let result = self.client.invoke_capability("Aspire.Hosting/withUrlForEndpoint", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Adds a URL for a specific endpoint via factory callback
    pub fn with_url_for_endpoint_factory(&self, endpoint_name: &str, callback: impl Fn(Vec<Value>) -> Value + Send + Sync + 'static) -> Result<IResourceWithEndpoints, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("endpointName".to_string(), serde_json::to_value(&endpoint_name).unwrap_or(Value::Null));
        let callback_id = register_callback(callback);
        args.insert("callback".to_string(), Value::String(callback_id));
        let result = self.client.invoke_capability("Aspire.Hosting/withUrlForEndpointFactory", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResourceWithEndpoints::new(handle, self.client.clone()))
    }

    /// Configures the resource to copy container files from the specified source during publishing
    pub fn publish_with_container_files(&self, source: &IResourceWithContainerFiles, destination_path: &str) -> Result<IContainerFilesDestinationResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("source".to_string(), source.handle().to_json());
        args.insert("destinationPath".to_string(), serde_json::to_value(&destination_path).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting/publishWithContainerFilesFromResource", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IContainerFilesDestinationResource::new(handle, self.client.clone()))
    }

    /// Excludes the resource from the deployment manifest
    pub fn exclude_from_manifest(&self) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        let result = self.client.invoke_capability("Aspire.Hosting/excludeFromManifest", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Waits for another resource to be ready
    pub fn wait_for(&self, dependency: &IResource) -> Result<IResourceWithWaitSupport, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("dependency".to_string(), dependency.handle().to_json());
        let result = self.client.invoke_capability("Aspire.Hosting/waitForResource", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResourceWithWaitSupport::new(handle, self.client.clone()))
    }

    /// Waits for another resource with specific behavior
    pub fn wait_for_with_behavior(&self, dependency: &IResource, wait_behavior: WaitBehavior) -> Result<IResourceWithWaitSupport, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("dependency".to_string(), dependency.handle().to_json());
        args.insert("waitBehavior".to_string(), serde_json::to_value(&wait_behavior).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting/waitForWithBehavior", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResourceWithWaitSupport::new(handle, self.client.clone()))
    }

    /// Waits for another resource to start
    pub fn wait_for_start(&self, dependency: &IResource) -> Result<IResourceWithWaitSupport, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("dependency".to_string(), dependency.handle().to_json());
        let result = self.client.invoke_capability("Aspire.Hosting/waitForResourceStart", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResourceWithWaitSupport::new(handle, self.client.clone()))
    }

    /// Waits for another resource to start with specific behavior
    pub fn wait_for_start_with_behavior(&self, dependency: &IResource, wait_behavior: WaitBehavior) -> Result<IResourceWithWaitSupport, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("dependency".to_string(), dependency.handle().to_json());
        args.insert("waitBehavior".to_string(), serde_json::to_value(&wait_behavior).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting/waitForStartWithBehavior", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResourceWithWaitSupport::new(handle, self.client.clone()))
    }

    /// Prevents resource from starting automatically
    pub fn with_explicit_start(&self) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        let result = self.client.invoke_capability("Aspire.Hosting/withExplicitStart", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Waits for resource completion
    pub fn wait_for_completion(&self, dependency: &IResource, exit_code: Option<f64>) -> Result<IResourceWithWaitSupport, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("dependency".to_string(), dependency.handle().to_json());
        if let Some(ref v) = exit_code {
            args.insert("exitCode".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        let result = self.client.invoke_capability("Aspire.Hosting/waitForResourceCompletion", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResourceWithWaitSupport::new(handle, self.client.clone()))
    }

    /// Adds a health check by key
    pub fn with_health_check(&self, key: &str) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("key".to_string(), serde_json::to_value(&key).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting/withHealthCheck", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Adds an HTTP health check
    pub fn with_http_health_check(&self, path: Option<&str>, status_code: Option<f64>, endpoint_name: Option<&str>) -> Result<IResourceWithEndpoints, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        if let Some(ref v) = path {
            args.insert("path".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        if let Some(ref v) = status_code {
            args.insert("statusCode".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        if let Some(ref v) = endpoint_name {
            args.insert("endpointName".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        let result = self.client.invoke_capability("Aspire.Hosting/withHttpHealthCheck", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResourceWithEndpoints::new(handle, self.client.clone()))
    }

    /// Adds a resource command
    pub fn with_command(&self, name: &str, display_name: &str, execute_command: impl Fn(Vec<Value>) -> Value + Send + Sync + 'static, command_options: Option<CommandOptions>) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("name".to_string(), serde_json::to_value(&name).unwrap_or(Value::Null));
        args.insert("displayName".to_string(), serde_json::to_value(&display_name).unwrap_or(Value::Null));
        let callback_id = register_callback(execute_command);
        args.insert("executeCommand".to_string(), Value::String(callback_id));
        if let Some(ref v) = command_options {
            args.insert("commandOptions".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        let result = self.client.invoke_capability("Aspire.Hosting/withCommand", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Configures developer certificate trust
    pub fn with_developer_certificate_trust(&self, trust: bool) -> Result<IResourceWithEnvironment, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("trust".to_string(), serde_json::to_value(&trust).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting/withDeveloperCertificateTrust", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResourceWithEnvironment::new(handle, self.client.clone()))
    }

    /// Sets the certificate trust scope
    pub fn with_certificate_trust_scope(&self, scope: CertificateTrustScope) -> Result<IResourceWithEnvironment, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("scope".to_string(), serde_json::to_value(&scope).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting/withCertificateTrustScope", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResourceWithEnvironment::new(handle, self.client.clone()))
    }

    /// Configures HTTPS with a developer certificate
    pub fn with_https_developer_certificate(&self, password: Option<&ParameterResource>) -> Result<IResourceWithEnvironment, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        if let Some(ref v) = password {
            args.insert("password".to_string(), v.handle().to_json());
        }
        let result = self.client.invoke_capability("Aspire.Hosting/withParameterHttpsDeveloperCertificate", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResourceWithEnvironment::new(handle, self.client.clone()))
    }

    /// Removes HTTPS certificate configuration
    pub fn without_https_certificate(&self) -> Result<IResourceWithEnvironment, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        let result = self.client.invoke_capability("Aspire.Hosting/withoutHttpsCertificate", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResourceWithEnvironment::new(handle, self.client.clone()))
    }

    /// Sets the parent relationship
    pub fn with_parent_relationship(&self, parent: &IResource) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("parent".to_string(), parent.handle().to_json());
        let result = self.client.invoke_capability("Aspire.Hosting/withBuilderParentRelationship", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Sets a child relationship
    pub fn with_child_relationship(&self, child: &IResource) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("child".to_string(), child.handle().to_json());
        let result = self.client.invoke_capability("Aspire.Hosting/withBuilderChildRelationship", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Sets the icon for the resource
    pub fn with_icon_name(&self, icon_name: &str, icon_variant: Option<IconVariant>) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("iconName".to_string(), serde_json::to_value(&icon_name).unwrap_or(Value::Null));
        if let Some(ref v) = icon_variant {
            args.insert("iconVariant".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        let result = self.client.invoke_capability("Aspire.Hosting/withIconName", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Adds an HTTP health probe to the resource
    pub fn with_http_probe(&self, probe_type: ProbeType, path: Option<&str>, initial_delay_seconds: Option<f64>, period_seconds: Option<f64>, timeout_seconds: Option<f64>, failure_threshold: Option<f64>, success_threshold: Option<f64>, endpoint_name: Option<&str>) -> Result<IResourceWithEndpoints, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("probeType".to_string(), serde_json::to_value(&probe_type).unwrap_or(Value::Null));
        if let Some(ref v) = path {
            args.insert("path".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        if let Some(ref v) = initial_delay_seconds {
            args.insert("initialDelaySeconds".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        if let Some(ref v) = period_seconds {
            args.insert("periodSeconds".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        if let Some(ref v) = timeout_seconds {
            args.insert("timeoutSeconds".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        if let Some(ref v) = failure_threshold {
            args.insert("failureThreshold".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        if let Some(ref v) = success_threshold {
            args.insert("successThreshold".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        if let Some(ref v) = endpoint_name {
            args.insert("endpointName".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        let result = self.client.invoke_capability("Aspire.Hosting/withHttpProbe", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResourceWithEndpoints::new(handle, self.client.clone()))
    }

    /// Excludes the resource from MCP server exposure
    pub fn exclude_from_mcp(&self) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        let result = self.client.invoke_capability("Aspire.Hosting/excludeFromMcp", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Sets the remote image name for publishing
    pub fn with_remote_image_name(&self, remote_image_name: &str) -> Result<IComputeResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("remoteImageName".to_string(), serde_json::to_value(&remote_image_name).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting/withRemoteImageName", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IComputeResource::new(handle, self.client.clone()))
    }

    /// Sets the remote image tag for publishing
    pub fn with_remote_image_tag(&self, remote_image_tag: &str) -> Result<IComputeResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("remoteImageTag".to_string(), serde_json::to_value(&remote_image_tag).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting/withRemoteImageTag", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IComputeResource::new(handle, self.client.clone()))
    }

    /// Adds a pipeline step to the resource
    pub fn with_pipeline_step_factory(&self, step_name: &str, callback: impl Fn(Vec<Value>) -> Value + Send + Sync + 'static, depends_on: Option<Vec<String>>, required_by: Option<Vec<String>>, tags: Option<Vec<String>>, description: Option<&str>) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("stepName".to_string(), serde_json::to_value(&step_name).unwrap_or(Value::Null));
        let callback_id = register_callback(callback);
        args.insert("callback".to_string(), Value::String(callback_id));
        if let Some(ref v) = depends_on {
            args.insert("dependsOn".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        if let Some(ref v) = required_by {
            args.insert("requiredBy".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        if let Some(ref v) = tags {
            args.insert("tags".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        if let Some(ref v) = description {
            args.insert("description".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        let result = self.client.invoke_capability("Aspire.Hosting/withPipelineStepFactory", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Configures pipeline step dependencies via a callback
    pub fn with_pipeline_configuration(&self, callback: impl Fn(Vec<Value>) -> Value + Send + Sync + 'static) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        let callback_id = register_callback(callback);
        args.insert("callback".to_string(), Value::String(callback_id));
        let result = self.client.invoke_capability("Aspire.Hosting/withPipelineConfiguration", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Gets the resource name
    pub fn get_resource_name(&self) -> Result<String, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("resource".to_string(), self.handle.to_json());
        let result = self.client.invoke_capability("Aspire.Hosting/getResourceName", args)?;
        Ok(serde_json::from_value(result)?)
    }

    /// Subscribes to the BeforeResourceStarted event
    pub fn on_before_resource_started(&self, callback: impl Fn(Vec<Value>) -> Value + Send + Sync + 'static) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        let callback_id = register_callback(callback);
        args.insert("callback".to_string(), Value::String(callback_id));
        let result = self.client.invoke_capability("Aspire.Hosting/onBeforeResourceStarted", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Subscribes to the ResourceStopped event
    pub fn on_resource_stopped(&self, callback: impl Fn(Vec<Value>) -> Value + Send + Sync + 'static) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        let callback_id = register_callback(callback);
        args.insert("callback".to_string(), Value::String(callback_id));
        let result = self.client.invoke_capability("Aspire.Hosting/onResourceStopped", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Subscribes to the InitializeResource event
    pub fn on_initialize_resource(&self, callback: impl Fn(Vec<Value>) -> Value + Send + Sync + 'static) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        let callback_id = register_callback(callback);
        args.insert("callback".to_string(), Value::String(callback_id));
        let result = self.client.invoke_capability("Aspire.Hosting/onInitializeResource", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Subscribes to the ResourceEndpointsAllocated event
    pub fn on_resource_endpoints_allocated(&self, callback: impl Fn(Vec<Value>) -> Value + Send + Sync + 'static) -> Result<IResourceWithEndpoints, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        let callback_id = register_callback(callback);
        args.insert("callback".to_string(), Value::String(callback_id));
        let result = self.client.invoke_capability("Aspire.Hosting/onResourceEndpointsAllocated", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResourceWithEndpoints::new(handle, self.client.clone()))
    }

    /// Subscribes to the ResourceReady event
    pub fn on_resource_ready(&self, callback: impl Fn(Vec<Value>) -> Value + Send + Sync + 'static) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        let callback_id = register_callback(callback);
        args.insert("callback".to_string(), Value::String(callback_id));
        let result = self.client.invoke_capability("Aspire.Hosting/onResourceReady", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
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

    /// Adds a dependency from a string or another resource
    pub fn with_union_dependency(&self, dependency: Value) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("dependency".to_string(), serde_json::to_value(&dependency).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting.CodeGeneration.Rust.Tests/withUnionDependency", args)?;
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

    /// Adds a label to the resource
    pub fn with_merge_label(&self, label: &str) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("label".to_string(), serde_json::to_value(&label).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting.CodeGeneration.Rust.Tests/withMergeLabel", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Adds a categorized label to the resource
    pub fn with_merge_label_categorized(&self, label: &str, category: &str) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("label".to_string(), serde_json::to_value(&label).unwrap_or(Value::Null));
        args.insert("category".to_string(), serde_json::to_value(&category).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting.CodeGeneration.Rust.Tests/withMergeLabelCategorized", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Configures a named endpoint
    pub fn with_merge_endpoint(&self, endpoint_name: &str, port: f64) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("endpointName".to_string(), serde_json::to_value(&endpoint_name).unwrap_or(Value::Null));
        args.insert("port".to_string(), serde_json::to_value(&port).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting.CodeGeneration.Rust.Tests/withMergeEndpoint", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Configures a named endpoint with scheme
    pub fn with_merge_endpoint_scheme(&self, endpoint_name: &str, port: f64, scheme: &str) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("endpointName".to_string(), serde_json::to_value(&endpoint_name).unwrap_or(Value::Null));
        args.insert("port".to_string(), serde_json::to_value(&port).unwrap_or(Value::Null));
        args.insert("scheme".to_string(), serde_json::to_value(&scheme).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting.CodeGeneration.Rust.Tests/withMergeEndpointScheme", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Configures resource logging
    pub fn with_merge_logging(&self, log_level: &str, enable_console: Option<bool>, max_files: Option<f64>) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("logLevel".to_string(), serde_json::to_value(&log_level).unwrap_or(Value::Null));
        if let Some(ref v) = enable_console {
            args.insert("enableConsole".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        if let Some(ref v) = max_files {
            args.insert("maxFiles".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        let result = self.client.invoke_capability("Aspire.Hosting.CodeGeneration.Rust.Tests/withMergeLogging", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Configures resource logging with file path
    pub fn with_merge_logging_path(&self, log_level: &str, log_path: &str, enable_console: Option<bool>, max_files: Option<f64>) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("logLevel".to_string(), serde_json::to_value(&log_level).unwrap_or(Value::Null));
        args.insert("logPath".to_string(), serde_json::to_value(&log_path).unwrap_or(Value::Null));
        if let Some(ref v) = enable_console {
            args.insert("enableConsole".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        if let Some(ref v) = max_files {
            args.insert("maxFiles".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        let result = self.client.invoke_capability("Aspire.Hosting.CodeGeneration.Rust.Tests/withMergeLoggingPath", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Configures a route
    pub fn with_merge_route(&self, path: &str, method: &str, handler: &str, priority: f64) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("path".to_string(), serde_json::to_value(&path).unwrap_or(Value::Null));
        args.insert("method".to_string(), serde_json::to_value(&method).unwrap_or(Value::Null));
        args.insert("handler".to_string(), serde_json::to_value(&handler).unwrap_or(Value::Null));
        args.insert("priority".to_string(), serde_json::to_value(&priority).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting.CodeGeneration.Rust.Tests/withMergeRoute", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Configures a route with middleware
    pub fn with_merge_route_middleware(&self, path: &str, method: &str, handler: &str, priority: f64, middleware: &str) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("path".to_string(), serde_json::to_value(&path).unwrap_or(Value::Null));
        args.insert("method".to_string(), serde_json::to_value(&method).unwrap_or(Value::Null));
        args.insert("handler".to_string(), serde_json::to_value(&handler).unwrap_or(Value::Null));
        args.insert("priority".to_string(), serde_json::to_value(&priority).unwrap_or(Value::Null));
        args.insert("middleware".to_string(), serde_json::to_value(&middleware).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting.CodeGeneration.Rust.Tests/withMergeRouteMiddleware", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }
}

/// Wrapper for Aspire.Hosting/Aspire.Hosting.ProjectResourceOptions
pub struct ProjectResourceOptions {
    handle: Handle,
    client: Arc<AspireClient>,
}

impl HasHandle for ProjectResourceOptions {
    fn handle(&self) -> &Handle {
        &self.handle
    }
}

impl ProjectResourceOptions {
    pub fn new(handle: Handle, client: Arc<AspireClient>) -> Self {
        Self { handle, client }
    }

    pub fn handle(&self) -> &Handle {
        &self.handle
    }

    pub fn client(&self) -> &Arc<AspireClient> {
        &self.client
    }

    /// Gets the LaunchProfileName property
    pub fn launch_profile_name(&self) -> Result<String, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("context".to_string(), self.handle.to_json());
        let result = self.client.invoke_capability("Aspire.Hosting/ProjectResourceOptions.launchProfileName", args)?;
        Ok(serde_json::from_value(result)?)
    }

    /// Sets the LaunchProfileName property
    pub fn set_launch_profile_name(&self, value: &str) -> Result<ProjectResourceOptions, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("context".to_string(), self.handle.to_json());
        args.insert("value".to_string(), serde_json::to_value(&value).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting/ProjectResourceOptions.setLaunchProfileName", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(ProjectResourceOptions::new(handle, self.client.clone()))
    }

    /// Gets the ExcludeLaunchProfile property
    pub fn exclude_launch_profile(&self) -> Result<bool, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("context".to_string(), self.handle.to_json());
        let result = self.client.invoke_capability("Aspire.Hosting/ProjectResourceOptions.excludeLaunchProfile", args)?;
        Ok(serde_json::from_value(result)?)
    }

    /// Sets the ExcludeLaunchProfile property
    pub fn set_exclude_launch_profile(&self, value: bool) -> Result<ProjectResourceOptions, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("context".to_string(), self.handle.to_json());
        args.insert("value".to_string(), serde_json::to_value(&value).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting/ProjectResourceOptions.setExcludeLaunchProfile", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(ProjectResourceOptions::new(handle, self.client.clone()))
    }

    /// Gets the ExcludeKestrelEndpoints property
    pub fn exclude_kestrel_endpoints(&self) -> Result<bool, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("context".to_string(), self.handle.to_json());
        let result = self.client.invoke_capability("Aspire.Hosting/ProjectResourceOptions.excludeKestrelEndpoints", args)?;
        Ok(serde_json::from_value(result)?)
    }

    /// Sets the ExcludeKestrelEndpoints property
    pub fn set_exclude_kestrel_endpoints(&self, value: bool) -> Result<ProjectResourceOptions, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("context".to_string(), self.handle.to_json());
        args.insert("value".to_string(), serde_json::to_value(&value).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting/ProjectResourceOptions.setExcludeKestrelEndpoints", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(ProjectResourceOptions::new(handle, self.client.clone()))
    }
}

/// Wrapper for Aspire.Hosting/Aspire.Hosting.ApplicationModel.ReferenceExpressionBuilder
pub struct ReferenceExpressionBuilder {
    handle: Handle,
    client: Arc<AspireClient>,
}

impl HasHandle for ReferenceExpressionBuilder {
    fn handle(&self) -> &Handle {
        &self.handle
    }
}

impl ReferenceExpressionBuilder {
    pub fn new(handle: Handle, client: Arc<AspireClient>) -> Self {
        Self { handle, client }
    }

    pub fn handle(&self) -> &Handle {
        &self.handle
    }

    pub fn client(&self) -> &Arc<AspireClient> {
        &self.client
    }

    /// Gets the IsEmpty property
    pub fn is_empty(&self) -> Result<bool, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("context".to_string(), self.handle.to_json());
        let result = self.client.invoke_capability("Aspire.Hosting.ApplicationModel/ReferenceExpressionBuilder.isEmpty", args)?;
        Ok(serde_json::from_value(result)?)
    }

    /// Appends a literal string to the reference expression
    pub fn append_literal(&self, value: &str) -> Result<(), Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("context".to_string(), self.handle.to_json());
        args.insert("value".to_string(), serde_json::to_value(&value).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting.ApplicationModel/appendLiteral", args)?;
        Ok(())
    }

    /// Appends a formatted string value to the reference expression
    pub fn append_formatted(&self, value: &str, format: Option<&str>) -> Result<(), Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("context".to_string(), self.handle.to_json());
        args.insert("value".to_string(), serde_json::to_value(&value).unwrap_or(Value::Null));
        if let Some(ref v) = format {
            args.insert("format".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        let result = self.client.invoke_capability("Aspire.Hosting.ApplicationModel/appendFormatted", args)?;
        Ok(())
    }

    /// Appends a value provider to the reference expression
    pub fn append_value_provider(&self, value_provider: &Value, format: Option<&str>) -> Result<(), Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("context".to_string(), self.handle.to_json());
        args.insert("valueProvider".to_string(), serde_json::to_value(&value_provider).unwrap_or(Value::Null));
        if let Some(ref v) = format {
            args.insert("format".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        let result = self.client.invoke_capability("Aspire.Hosting.ApplicationModel/appendValueProvider", args)?;
        Ok(())
    }

    /// Builds the reference expression
    pub fn build(&self) -> Result<ReferenceExpression, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("context".to_string(), self.handle.to_json());
        let result = self.client.invoke_capability("Aspire.Hosting.ApplicationModel/build", args)?;
        Ok(serde_json::from_value(result)?)
    }
}

/// Wrapper for Aspire.Hosting/Aspire.Hosting.ApplicationModel.ResourceEndpointsAllocatedEvent
pub struct ResourceEndpointsAllocatedEvent {
    handle: Handle,
    client: Arc<AspireClient>,
}

impl HasHandle for ResourceEndpointsAllocatedEvent {
    fn handle(&self) -> &Handle {
        &self.handle
    }
}

impl ResourceEndpointsAllocatedEvent {
    pub fn new(handle: Handle, client: Arc<AspireClient>) -> Self {
        Self { handle, client }
    }

    pub fn handle(&self) -> &Handle {
        &self.handle
    }

    pub fn client(&self) -> &Arc<AspireClient> {
        &self.client
    }

    /// Gets the Resource property
    pub fn resource(&self) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("context".to_string(), self.handle.to_json());
        let result = self.client.invoke_capability("Aspire.Hosting.ApplicationModel/ResourceEndpointsAllocatedEvent.resource", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Gets the Services property
    pub fn services(&self) -> Result<IServiceProvider, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("context".to_string(), self.handle.to_json());
        let result = self.client.invoke_capability("Aspire.Hosting.ApplicationModel/ResourceEndpointsAllocatedEvent.services", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IServiceProvider::new(handle, self.client.clone()))
    }
}

/// Wrapper for Aspire.Hosting/Aspire.Hosting.ApplicationModel.ResourceLoggerService
pub struct ResourceLoggerService {
    handle: Handle,
    client: Arc<AspireClient>,
}

impl HasHandle for ResourceLoggerService {
    fn handle(&self) -> &Handle {
        &self.handle
    }
}

impl ResourceLoggerService {
    pub fn new(handle: Handle, client: Arc<AspireClient>) -> Self {
        Self { handle, client }
    }

    pub fn handle(&self) -> &Handle {
        &self.handle
    }

    pub fn client(&self) -> &Arc<AspireClient> {
        &self.client
    }

    /// Completes the log stream for a resource
    pub fn complete_log(&self, resource: &IResource) -> Result<(), Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("loggerService".to_string(), self.handle.to_json());
        args.insert("resource".to_string(), resource.handle().to_json());
        let result = self.client.invoke_capability("Aspire.Hosting/completeLog", args)?;
        Ok(())
    }

    /// Completes the log stream by resource name
    pub fn complete_log_by_name(&self, resource_name: &str) -> Result<(), Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("loggerService".to_string(), self.handle.to_json());
        args.insert("resourceName".to_string(), serde_json::to_value(&resource_name).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting/completeLogByName", args)?;
        Ok(())
    }
}

/// Wrapper for Aspire.Hosting/Aspire.Hosting.ApplicationModel.ResourceNotificationService
pub struct ResourceNotificationService {
    handle: Handle,
    client: Arc<AspireClient>,
}

impl HasHandle for ResourceNotificationService {
    fn handle(&self) -> &Handle {
        &self.handle
    }
}

impl ResourceNotificationService {
    pub fn new(handle: Handle, client: Arc<AspireClient>) -> Self {
        Self { handle, client }
    }

    pub fn handle(&self) -> &Handle {
        &self.handle
    }

    pub fn client(&self) -> &Arc<AspireClient> {
        &self.client
    }

    /// Waits for a resource to reach a specified state
    pub fn wait_for_resource_state(&self, resource_name: &str, target_state: Option<&str>) -> Result<(), Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("notificationService".to_string(), self.handle.to_json());
        args.insert("resourceName".to_string(), serde_json::to_value(&resource_name).unwrap_or(Value::Null));
        if let Some(ref v) = target_state {
            args.insert("targetState".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        let result = self.client.invoke_capability("Aspire.Hosting/waitForResourceState", args)?;
        Ok(())
    }

    /// Waits for a resource to reach one of the specified states
    pub fn wait_for_resource_states(&self, resource_name: &str, target_states: Vec<String>) -> Result<String, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("notificationService".to_string(), self.handle.to_json());
        args.insert("resourceName".to_string(), serde_json::to_value(&resource_name).unwrap_or(Value::Null));
        args.insert("targetStates".to_string(), serde_json::to_value(&target_states).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting/waitForResourceStates", args)?;
        Ok(serde_json::from_value(result)?)
    }

    /// Waits for a resource to become healthy
    pub fn wait_for_resource_healthy(&self, resource_name: &str) -> Result<ResourceEventDto, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("notificationService".to_string(), self.handle.to_json());
        args.insert("resourceName".to_string(), serde_json::to_value(&resource_name).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting/waitForResourceHealthy", args)?;
        Ok(serde_json::from_value(result)?)
    }

    /// Waits for all dependencies of a resource to be ready
    pub fn wait_for_dependencies(&self, resource: &IResource) -> Result<(), Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("notificationService".to_string(), self.handle.to_json());
        args.insert("resource".to_string(), resource.handle().to_json());
        let result = self.client.invoke_capability("Aspire.Hosting/waitForDependencies", args)?;
        Ok(())
    }

    /// Tries to get the current state of a resource
    pub fn try_get_resource_state(&self, resource_name: &str) -> Result<ResourceEventDto, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("notificationService".to_string(), self.handle.to_json());
        args.insert("resourceName".to_string(), serde_json::to_value(&resource_name).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting/tryGetResourceState", args)?;
        Ok(serde_json::from_value(result)?)
    }

    /// Publishes an update for a resource's state
    pub fn publish_resource_update(&self, resource: &IResource, state: Option<&str>, state_style: Option<&str>) -> Result<(), Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("notificationService".to_string(), self.handle.to_json());
        args.insert("resource".to_string(), resource.handle().to_json());
        if let Some(ref v) = state {
            args.insert("state".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        if let Some(ref v) = state_style {
            args.insert("stateStyle".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        let result = self.client.invoke_capability("Aspire.Hosting/publishResourceUpdate", args)?;
        Ok(())
    }
}

/// Wrapper for Aspire.Hosting/Aspire.Hosting.ApplicationModel.ResourceReadyEvent
pub struct ResourceReadyEvent {
    handle: Handle,
    client: Arc<AspireClient>,
}

impl HasHandle for ResourceReadyEvent {
    fn handle(&self) -> &Handle {
        &self.handle
    }
}

impl ResourceReadyEvent {
    pub fn new(handle: Handle, client: Arc<AspireClient>) -> Self {
        Self { handle, client }
    }

    pub fn handle(&self) -> &Handle {
        &self.handle
    }

    pub fn client(&self) -> &Arc<AspireClient> {
        &self.client
    }

    /// Gets the Resource property
    pub fn resource(&self) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("context".to_string(), self.handle.to_json());
        let result = self.client.invoke_capability("Aspire.Hosting.ApplicationModel/ResourceReadyEvent.resource", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Gets the Services property
    pub fn services(&self) -> Result<IServiceProvider, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("context".to_string(), self.handle.to_json());
        let result = self.client.invoke_capability("Aspire.Hosting.ApplicationModel/ResourceReadyEvent.services", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IServiceProvider::new(handle, self.client.clone()))
    }
}

/// Wrapper for Aspire.Hosting/Aspire.Hosting.ApplicationModel.ResourceStoppedEvent
pub struct ResourceStoppedEvent {
    handle: Handle,
    client: Arc<AspireClient>,
}

impl HasHandle for ResourceStoppedEvent {
    fn handle(&self) -> &Handle {
        &self.handle
    }
}

impl ResourceStoppedEvent {
    pub fn new(handle: Handle, client: Arc<AspireClient>) -> Self {
        Self { handle, client }
    }

    pub fn handle(&self) -> &Handle {
        &self.handle
    }

    pub fn client(&self) -> &Arc<AspireClient> {
        &self.client
    }

    /// Gets the Resource property
    pub fn resource(&self) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("context".to_string(), self.handle.to_json());
        let result = self.client.invoke_capability("Aspire.Hosting.ApplicationModel/ResourceStoppedEvent.resource", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Gets the Services property
    pub fn services(&self) -> Result<IServiceProvider, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("context".to_string(), self.handle.to_json());
        let result = self.client.invoke_capability("Aspire.Hosting.ApplicationModel/ResourceStoppedEvent.services", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IServiceProvider::new(handle, self.client.clone()))
    }
}

/// Wrapper for Aspire.Hosting/Aspire.Hosting.ApplicationModel.ResourceUrlsCallbackContext
pub struct ResourceUrlsCallbackContext {
    handle: Handle,
    client: Arc<AspireClient>,
}

impl HasHandle for ResourceUrlsCallbackContext {
    fn handle(&self) -> &Handle {
        &self.handle
    }
}

impl ResourceUrlsCallbackContext {
    pub fn new(handle: Handle, client: Arc<AspireClient>) -> Self {
        Self { handle, client }
    }

    pub fn handle(&self) -> &Handle {
        &self.handle
    }

    pub fn client(&self) -> &Arc<AspireClient> {
        &self.client
    }

    /// Gets the Resource property
    pub fn resource(&self) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("context".to_string(), self.handle.to_json());
        let result = self.client.invoke_capability("Aspire.Hosting.ApplicationModel/ResourceUrlsCallbackContext.resource", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Gets the Urls property
    pub fn urls(&self) -> AspireList<ResourceUrlAnnotation> {
        AspireList::with_getter(self.handle.clone(), self.client.clone(), "Aspire.Hosting.ApplicationModel/ResourceUrlsCallbackContext.urls")
    }

    /// Gets the CancellationToken property
    pub fn cancellation_token(&self) -> Result<CancellationToken, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("context".to_string(), self.handle.to_json());
        let result = self.client.invoke_capability("Aspire.Hosting.ApplicationModel/ResourceUrlsCallbackContext.cancellationToken", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(CancellationToken::new(handle, self.client.clone()))
    }

    /// Gets the Logger property
    pub fn logger(&self) -> Result<ILogger, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("context".to_string(), self.handle.to_json());
        let result = self.client.invoke_capability("Aspire.Hosting.ApplicationModel/ResourceUrlsCallbackContext.logger", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(ILogger::new(handle, self.client.clone()))
    }

    /// Sets the Logger property
    pub fn set_logger(&self, value: &ILogger) -> Result<ResourceUrlsCallbackContext, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("context".to_string(), self.handle.to_json());
        args.insert("value".to_string(), value.handle().to_json());
        let result = self.client.invoke_capability("Aspire.Hosting.ApplicationModel/ResourceUrlsCallbackContext.setLogger", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(ResourceUrlsCallbackContext::new(handle, self.client.clone()))
    }

    /// Gets the ExecutionContext property
    pub fn execution_context(&self) -> Result<DistributedApplicationExecutionContext, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("context".to_string(), self.handle.to_json());
        let result = self.client.invoke_capability("Aspire.Hosting.ApplicationModel/ResourceUrlsCallbackContext.executionContext", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(DistributedApplicationExecutionContext::new(handle, self.client.clone()))
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

    /// Configures a resource to use a container registry
    pub fn with_container_registry(&self, registry: &IResource) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("registry".to_string(), registry.handle().to_json());
        let result = self.client.invoke_capability("Aspire.Hosting/withContainerRegistry", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Adds a bind mount
    pub fn with_bind_mount(&self, source: &str, target: &str, is_read_only: Option<bool>) -> Result<ContainerResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("source".to_string(), serde_json::to_value(&source).unwrap_or(Value::Null));
        args.insert("target".to_string(), serde_json::to_value(&target).unwrap_or(Value::Null));
        if let Some(ref v) = is_read_only {
            args.insert("isReadOnly".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        let result = self.client.invoke_capability("Aspire.Hosting/withBindMount", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(ContainerResource::new(handle, self.client.clone()))
    }

    /// Sets the container entrypoint
    pub fn with_entrypoint(&self, entrypoint: &str) -> Result<ContainerResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("entrypoint".to_string(), serde_json::to_value(&entrypoint).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting/withEntrypoint", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(ContainerResource::new(handle, self.client.clone()))
    }

    /// Sets the container image tag
    pub fn with_image_tag(&self, tag: &str) -> Result<ContainerResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("tag".to_string(), serde_json::to_value(&tag).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting/withImageTag", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(ContainerResource::new(handle, self.client.clone()))
    }

    /// Sets the container image registry
    pub fn with_image_registry(&self, registry: &str) -> Result<ContainerResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("registry".to_string(), serde_json::to_value(&registry).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting/withImageRegistry", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(ContainerResource::new(handle, self.client.clone()))
    }

    /// Sets the container image
    pub fn with_image(&self, image: &str, tag: Option<&str>) -> Result<ContainerResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("image".to_string(), serde_json::to_value(&image).unwrap_or(Value::Null));
        if let Some(ref v) = tag {
            args.insert("tag".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        let result = self.client.invoke_capability("Aspire.Hosting/withImage", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(ContainerResource::new(handle, self.client.clone()))
    }

    /// Sets the image SHA256 digest
    pub fn with_image_sha256(&self, sha256: &str) -> Result<ContainerResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("sha256".to_string(), serde_json::to_value(&sha256).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting/withImageSHA256", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(ContainerResource::new(handle, self.client.clone()))
    }

    /// Adds runtime arguments for the container
    pub fn with_container_runtime_args(&self, args: Vec<String>) -> Result<ContainerResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("args".to_string(), serde_json::to_value(&args).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting/withContainerRuntimeArgs", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(ContainerResource::new(handle, self.client.clone()))
    }

    /// Sets the lifetime behavior of the container resource
    pub fn with_lifetime(&self, lifetime: ContainerLifetime) -> Result<ContainerResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("lifetime".to_string(), serde_json::to_value(&lifetime).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting/withLifetime", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(ContainerResource::new(handle, self.client.clone()))
    }

    /// Sets the container image pull policy
    pub fn with_image_pull_policy(&self, pull_policy: ImagePullPolicy) -> Result<ContainerResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("pullPolicy".to_string(), serde_json::to_value(&pull_policy).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting/withImagePullPolicy", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(ContainerResource::new(handle, self.client.clone()))
    }

    /// Configures the resource to be published as a container
    pub fn publish_as_container(&self) -> Result<ContainerResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        let result = self.client.invoke_capability("Aspire.Hosting/publishAsContainer", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(ContainerResource::new(handle, self.client.clone()))
    }

    /// Configures the resource to use a Dockerfile
    pub fn with_dockerfile(&self, context_path: &str, dockerfile_path: Option<&str>, stage: Option<&str>) -> Result<ContainerResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("contextPath".to_string(), serde_json::to_value(&context_path).unwrap_or(Value::Null));
        if let Some(ref v) = dockerfile_path {
            args.insert("dockerfilePath".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        if let Some(ref v) = stage {
            args.insert("stage".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        let result = self.client.invoke_capability("Aspire.Hosting/withDockerfile", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(ContainerResource::new(handle, self.client.clone()))
    }

    /// Sets the container name
    pub fn with_container_name(&self, name: &str) -> Result<ContainerResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("name".to_string(), serde_json::to_value(&name).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting/withContainerName", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(ContainerResource::new(handle, self.client.clone()))
    }

    /// Adds a build argument from a parameter resource
    pub fn with_build_arg(&self, name: &str, value: &ParameterResource) -> Result<ContainerResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("name".to_string(), serde_json::to_value(&name).unwrap_or(Value::Null));
        args.insert("value".to_string(), value.handle().to_json());
        let result = self.client.invoke_capability("Aspire.Hosting/withParameterBuildArg", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(ContainerResource::new(handle, self.client.clone()))
    }

    /// Adds a build secret from a parameter resource
    pub fn with_build_secret(&self, name: &str, value: &ParameterResource) -> Result<ContainerResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("name".to_string(), serde_json::to_value(&name).unwrap_or(Value::Null));
        args.insert("value".to_string(), value.handle().to_json());
        let result = self.client.invoke_capability("Aspire.Hosting/withParameterBuildSecret", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(ContainerResource::new(handle, self.client.clone()))
    }

    /// Configures endpoint proxy support
    pub fn with_endpoint_proxy_support(&self, proxy_enabled: bool) -> Result<ContainerResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("proxyEnabled".to_string(), serde_json::to_value(&proxy_enabled).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting/withEndpointProxySupport", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(ContainerResource::new(handle, self.client.clone()))
    }

    /// Sets the base image for a Dockerfile build
    pub fn with_dockerfile_base_image(&self, build_image: Option<&str>, runtime_image: Option<&str>) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        if let Some(ref v) = build_image {
            args.insert("buildImage".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        if let Some(ref v) = runtime_image {
            args.insert("runtimeImage".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        let result = self.client.invoke_capability("Aspire.Hosting/withDockerfileBaseImage", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Adds a network alias for the container
    pub fn with_container_network_alias(&self, alias: &str) -> Result<ContainerResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("alias".to_string(), serde_json::to_value(&alias).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting/withContainerNetworkAlias", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(ContainerResource::new(handle, self.client.clone()))
    }

    /// Configures an MCP server endpoint on the resource
    pub fn with_mcp_server(&self, path: Option<&str>, endpoint_name: Option<&str>) -> Result<IResourceWithEndpoints, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        if let Some(ref v) = path {
            args.insert("path".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        if let Some(ref v) = endpoint_name {
            args.insert("endpointName".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        let result = self.client.invoke_capability("Aspire.Hosting/withMcpServer", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResourceWithEndpoints::new(handle, self.client.clone()))
    }

    /// Configures OTLP telemetry export
    pub fn with_otlp_exporter(&self) -> Result<IResourceWithEnvironment, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        let result = self.client.invoke_capability("Aspire.Hosting/withOtlpExporter", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResourceWithEnvironment::new(handle, self.client.clone()))
    }

    /// Configures OTLP telemetry export with specific protocol
    pub fn with_otlp_exporter_protocol(&self, protocol: OtlpProtocol) -> Result<IResourceWithEnvironment, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("protocol".to_string(), serde_json::to_value(&protocol).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting/withOtlpExporterProtocol", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResourceWithEnvironment::new(handle, self.client.clone()))
    }

    /// Publishes the resource as a connection string
    pub fn publish_as_connection_string(&self) -> Result<ContainerResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        let result = self.client.invoke_capability("Aspire.Hosting/publishAsConnectionString", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(ContainerResource::new(handle, self.client.clone()))
    }

    /// Adds a required command dependency
    pub fn with_required_command(&self, command: &str, help_link: Option<&str>) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("command".to_string(), serde_json::to_value(&command).unwrap_or(Value::Null));
        if let Some(ref v) = help_link {
            args.insert("helpLink".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        let result = self.client.invoke_capability("Aspire.Hosting/withRequiredCommand", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Sets an environment variable
    pub fn with_environment(&self, name: &str, value: Value) -> Result<IResourceWithEnvironment, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("name".to_string(), serde_json::to_value(&name).unwrap_or(Value::Null));
        args.insert("value".to_string(), serde_json::to_value(&value).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting/withEnvironment", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResourceWithEnvironment::new(handle, self.client.clone()))
    }

    /// Sets an environment variable from a reference expression
    pub fn with_environment_expression(&self, name: &str, value: ReferenceExpression) -> Result<IResourceWithEnvironment, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("name".to_string(), serde_json::to_value(&name).unwrap_or(Value::Null));
        args.insert("value".to_string(), serde_json::to_value(&value).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting/withEnvironmentExpression", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResourceWithEnvironment::new(handle, self.client.clone()))
    }

    /// Sets environment variables via callback
    pub fn with_environment_callback(&self, callback: impl Fn(Vec<Value>) -> Value + Send + Sync + 'static) -> Result<IResourceWithEnvironment, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        let callback_id = register_callback(callback);
        args.insert("callback".to_string(), Value::String(callback_id));
        let result = self.client.invoke_capability("Aspire.Hosting/withEnvironmentCallback", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResourceWithEnvironment::new(handle, self.client.clone()))
    }

    /// Sets an environment variable from an endpoint reference
    pub fn with_environment_endpoint(&self, name: &str, endpoint_reference: &EndpointReference) -> Result<IResourceWithEnvironment, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("name".to_string(), serde_json::to_value(&name).unwrap_or(Value::Null));
        args.insert("endpointReference".to_string(), endpoint_reference.handle().to_json());
        let result = self.client.invoke_capability("Aspire.Hosting/withEnvironmentEndpoint", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResourceWithEnvironment::new(handle, self.client.clone()))
    }

    /// Sets an environment variable from a parameter resource
    pub fn with_environment_parameter(&self, name: &str, parameter: &ParameterResource) -> Result<IResourceWithEnvironment, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("name".to_string(), serde_json::to_value(&name).unwrap_or(Value::Null));
        args.insert("parameter".to_string(), parameter.handle().to_json());
        let result = self.client.invoke_capability("Aspire.Hosting/withEnvironmentParameter", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResourceWithEnvironment::new(handle, self.client.clone()))
    }

    /// Sets an environment variable from a connection string resource
    pub fn with_environment_connection_string(&self, env_var_name: &str, resource: &IResourceWithConnectionString) -> Result<IResourceWithEnvironment, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("envVarName".to_string(), serde_json::to_value(&env_var_name).unwrap_or(Value::Null));
        args.insert("resource".to_string(), resource.handle().to_json());
        let result = self.client.invoke_capability("Aspire.Hosting/withEnvironmentConnectionString", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResourceWithEnvironment::new(handle, self.client.clone()))
    }

    /// Adds arguments
    pub fn with_args(&self, args: Vec<String>) -> Result<IResourceWithArgs, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("args".to_string(), serde_json::to_value(&args).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting/withArgs", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResourceWithArgs::new(handle, self.client.clone()))
    }

    /// Sets command-line arguments via callback
    pub fn with_args_callback(&self, callback: impl Fn(Vec<Value>) -> Value + Send + Sync + 'static) -> Result<IResourceWithArgs, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        let callback_id = register_callback(callback);
        args.insert("callback".to_string(), Value::String(callback_id));
        let result = self.client.invoke_capability("Aspire.Hosting/withArgsCallback", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResourceWithArgs::new(handle, self.client.clone()))
    }

    /// Adds a reference to another resource
    pub fn with_reference(&self, source: &IResource, connection_name: Option<&str>, optional: Option<bool>, name: Option<&str>) -> Result<IResourceWithEnvironment, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("source".to_string(), source.handle().to_json());
        if let Some(ref v) = connection_name {
            args.insert("connectionName".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        if let Some(ref v) = optional {
            args.insert("optional".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        if let Some(ref v) = name {
            args.insert("name".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        let result = self.client.invoke_capability("Aspire.Hosting/withReference", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResourceWithEnvironment::new(handle, self.client.clone()))
    }

    /// Adds a reference to a URI
    pub fn with_reference_uri(&self, name: &str, uri: &str) -> Result<IResourceWithEnvironment, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("name".to_string(), serde_json::to_value(&name).unwrap_or(Value::Null));
        args.insert("uri".to_string(), serde_json::to_value(&uri).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting/withReferenceUri", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResourceWithEnvironment::new(handle, self.client.clone()))
    }

    /// Adds a reference to an external service
    pub fn with_reference_external_service(&self, external_service: &ExternalServiceResource) -> Result<IResourceWithEnvironment, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("externalService".to_string(), external_service.handle().to_json());
        let result = self.client.invoke_capability("Aspire.Hosting/withReferenceExternalService", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResourceWithEnvironment::new(handle, self.client.clone()))
    }

    /// Adds a reference to an endpoint
    pub fn with_reference_endpoint(&self, endpoint_reference: &EndpointReference) -> Result<IResourceWithEnvironment, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("endpointReference".to_string(), endpoint_reference.handle().to_json());
        let result = self.client.invoke_capability("Aspire.Hosting/withReferenceEndpoint", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResourceWithEnvironment::new(handle, self.client.clone()))
    }

    /// Adds a network endpoint
    pub fn with_endpoint(&self, port: Option<f64>, target_port: Option<f64>, scheme: Option<&str>, name: Option<&str>, env: Option<&str>, is_proxied: Option<bool>, is_external: Option<bool>, protocol: Option<ProtocolType>) -> Result<IResourceWithEndpoints, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        if let Some(ref v) = port {
            args.insert("port".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        if let Some(ref v) = target_port {
            args.insert("targetPort".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        if let Some(ref v) = scheme {
            args.insert("scheme".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        if let Some(ref v) = name {
            args.insert("name".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        if let Some(ref v) = env {
            args.insert("env".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        if let Some(ref v) = is_proxied {
            args.insert("isProxied".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        if let Some(ref v) = is_external {
            args.insert("isExternal".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        if let Some(ref v) = protocol {
            args.insert("protocol".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        let result = self.client.invoke_capability("Aspire.Hosting/withEndpoint", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResourceWithEndpoints::new(handle, self.client.clone()))
    }

    /// Adds an HTTP endpoint
    pub fn with_http_endpoint(&self, port: Option<f64>, target_port: Option<f64>, name: Option<&str>, env: Option<&str>, is_proxied: Option<bool>) -> Result<IResourceWithEndpoints, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        if let Some(ref v) = port {
            args.insert("port".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        if let Some(ref v) = target_port {
            args.insert("targetPort".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        if let Some(ref v) = name {
            args.insert("name".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        if let Some(ref v) = env {
            args.insert("env".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        if let Some(ref v) = is_proxied {
            args.insert("isProxied".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        let result = self.client.invoke_capability("Aspire.Hosting/withHttpEndpoint", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResourceWithEndpoints::new(handle, self.client.clone()))
    }

    /// Adds an HTTPS endpoint
    pub fn with_https_endpoint(&self, port: Option<f64>, target_port: Option<f64>, name: Option<&str>, env: Option<&str>, is_proxied: Option<bool>) -> Result<IResourceWithEndpoints, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        if let Some(ref v) = port {
            args.insert("port".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        if let Some(ref v) = target_port {
            args.insert("targetPort".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        if let Some(ref v) = name {
            args.insert("name".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        if let Some(ref v) = env {
            args.insert("env".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        if let Some(ref v) = is_proxied {
            args.insert("isProxied".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        let result = self.client.invoke_capability("Aspire.Hosting/withHttpsEndpoint", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResourceWithEndpoints::new(handle, self.client.clone()))
    }

    /// Makes HTTP endpoints externally accessible
    pub fn with_external_http_endpoints(&self) -> Result<IResourceWithEndpoints, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        let result = self.client.invoke_capability("Aspire.Hosting/withExternalHttpEndpoints", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResourceWithEndpoints::new(handle, self.client.clone()))
    }

    /// Gets an endpoint reference
    pub fn get_endpoint(&self, name: &str) -> Result<EndpointReference, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("name".to_string(), serde_json::to_value(&name).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting/getEndpoint", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(EndpointReference::new(handle, self.client.clone()))
    }

    /// Configures resource for HTTP/2
    pub fn as_http2_service(&self) -> Result<IResourceWithEndpoints, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        let result = self.client.invoke_capability("Aspire.Hosting/asHttp2Service", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResourceWithEndpoints::new(handle, self.client.clone()))
    }

    /// Customizes displayed URLs via callback
    pub fn with_urls_callback(&self, callback: impl Fn(Vec<Value>) -> Value + Send + Sync + 'static) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        let callback_id = register_callback(callback);
        args.insert("callback".to_string(), Value::String(callback_id));
        let result = self.client.invoke_capability("Aspire.Hosting/withUrlsCallback", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Adds or modifies displayed URLs
    pub fn with_url(&self, url: &str, display_text: Option<&str>) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("url".to_string(), serde_json::to_value(&url).unwrap_or(Value::Null));
        if let Some(ref v) = display_text {
            args.insert("displayText".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        let result = self.client.invoke_capability("Aspire.Hosting/withUrl", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Adds a URL using a reference expression
    pub fn with_url_expression(&self, url: ReferenceExpression, display_text: Option<&str>) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("url".to_string(), serde_json::to_value(&url).unwrap_or(Value::Null));
        if let Some(ref v) = display_text {
            args.insert("displayText".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        let result = self.client.invoke_capability("Aspire.Hosting/withUrlExpression", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Customizes the URL for a specific endpoint via callback
    pub fn with_url_for_endpoint(&self, endpoint_name: &str, callback: impl Fn(Vec<Value>) -> Value + Send + Sync + 'static) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("endpointName".to_string(), serde_json::to_value(&endpoint_name).unwrap_or(Value::Null));
        let callback_id = register_callback(callback);
        args.insert("callback".to_string(), Value::String(callback_id));
        let result = self.client.invoke_capability("Aspire.Hosting/withUrlForEndpoint", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Adds a URL for a specific endpoint via factory callback
    pub fn with_url_for_endpoint_factory(&self, endpoint_name: &str, callback: impl Fn(Vec<Value>) -> Value + Send + Sync + 'static) -> Result<IResourceWithEndpoints, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("endpointName".to_string(), serde_json::to_value(&endpoint_name).unwrap_or(Value::Null));
        let callback_id = register_callback(callback);
        args.insert("callback".to_string(), Value::String(callback_id));
        let result = self.client.invoke_capability("Aspire.Hosting/withUrlForEndpointFactory", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResourceWithEndpoints::new(handle, self.client.clone()))
    }

    /// Excludes the resource from the deployment manifest
    pub fn exclude_from_manifest(&self) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        let result = self.client.invoke_capability("Aspire.Hosting/excludeFromManifest", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Waits for another resource to be ready
    pub fn wait_for(&self, dependency: &IResource) -> Result<IResourceWithWaitSupport, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("dependency".to_string(), dependency.handle().to_json());
        let result = self.client.invoke_capability("Aspire.Hosting/waitForResource", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResourceWithWaitSupport::new(handle, self.client.clone()))
    }

    /// Waits for another resource with specific behavior
    pub fn wait_for_with_behavior(&self, dependency: &IResource, wait_behavior: WaitBehavior) -> Result<IResourceWithWaitSupport, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("dependency".to_string(), dependency.handle().to_json());
        args.insert("waitBehavior".to_string(), serde_json::to_value(&wait_behavior).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting/waitForWithBehavior", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResourceWithWaitSupport::new(handle, self.client.clone()))
    }

    /// Waits for another resource to start
    pub fn wait_for_start(&self, dependency: &IResource) -> Result<IResourceWithWaitSupport, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("dependency".to_string(), dependency.handle().to_json());
        let result = self.client.invoke_capability("Aspire.Hosting/waitForResourceStart", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResourceWithWaitSupport::new(handle, self.client.clone()))
    }

    /// Waits for another resource to start with specific behavior
    pub fn wait_for_start_with_behavior(&self, dependency: &IResource, wait_behavior: WaitBehavior) -> Result<IResourceWithWaitSupport, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("dependency".to_string(), dependency.handle().to_json());
        args.insert("waitBehavior".to_string(), serde_json::to_value(&wait_behavior).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting/waitForStartWithBehavior", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResourceWithWaitSupport::new(handle, self.client.clone()))
    }

    /// Prevents resource from starting automatically
    pub fn with_explicit_start(&self) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        let result = self.client.invoke_capability("Aspire.Hosting/withExplicitStart", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Waits for resource completion
    pub fn wait_for_completion(&self, dependency: &IResource, exit_code: Option<f64>) -> Result<IResourceWithWaitSupport, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("dependency".to_string(), dependency.handle().to_json());
        if let Some(ref v) = exit_code {
            args.insert("exitCode".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        let result = self.client.invoke_capability("Aspire.Hosting/waitForResourceCompletion", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResourceWithWaitSupport::new(handle, self.client.clone()))
    }

    /// Adds a health check by key
    pub fn with_health_check(&self, key: &str) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("key".to_string(), serde_json::to_value(&key).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting/withHealthCheck", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Adds an HTTP health check
    pub fn with_http_health_check(&self, path: Option<&str>, status_code: Option<f64>, endpoint_name: Option<&str>) -> Result<IResourceWithEndpoints, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        if let Some(ref v) = path {
            args.insert("path".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        if let Some(ref v) = status_code {
            args.insert("statusCode".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        if let Some(ref v) = endpoint_name {
            args.insert("endpointName".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        let result = self.client.invoke_capability("Aspire.Hosting/withHttpHealthCheck", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResourceWithEndpoints::new(handle, self.client.clone()))
    }

    /// Adds a resource command
    pub fn with_command(&self, name: &str, display_name: &str, execute_command: impl Fn(Vec<Value>) -> Value + Send + Sync + 'static, command_options: Option<CommandOptions>) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("name".to_string(), serde_json::to_value(&name).unwrap_or(Value::Null));
        args.insert("displayName".to_string(), serde_json::to_value(&display_name).unwrap_or(Value::Null));
        let callback_id = register_callback(execute_command);
        args.insert("executeCommand".to_string(), Value::String(callback_id));
        if let Some(ref v) = command_options {
            args.insert("commandOptions".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        let result = self.client.invoke_capability("Aspire.Hosting/withCommand", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Configures developer certificate trust
    pub fn with_developer_certificate_trust(&self, trust: bool) -> Result<IResourceWithEnvironment, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("trust".to_string(), serde_json::to_value(&trust).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting/withDeveloperCertificateTrust", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResourceWithEnvironment::new(handle, self.client.clone()))
    }

    /// Sets the certificate trust scope
    pub fn with_certificate_trust_scope(&self, scope: CertificateTrustScope) -> Result<IResourceWithEnvironment, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("scope".to_string(), serde_json::to_value(&scope).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting/withCertificateTrustScope", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResourceWithEnvironment::new(handle, self.client.clone()))
    }

    /// Configures HTTPS with a developer certificate
    pub fn with_https_developer_certificate(&self, password: Option<&ParameterResource>) -> Result<IResourceWithEnvironment, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        if let Some(ref v) = password {
            args.insert("password".to_string(), v.handle().to_json());
        }
        let result = self.client.invoke_capability("Aspire.Hosting/withParameterHttpsDeveloperCertificate", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResourceWithEnvironment::new(handle, self.client.clone()))
    }

    /// Removes HTTPS certificate configuration
    pub fn without_https_certificate(&self) -> Result<IResourceWithEnvironment, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        let result = self.client.invoke_capability("Aspire.Hosting/withoutHttpsCertificate", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResourceWithEnvironment::new(handle, self.client.clone()))
    }

    /// Sets the parent relationship
    pub fn with_parent_relationship(&self, parent: &IResource) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("parent".to_string(), parent.handle().to_json());
        let result = self.client.invoke_capability("Aspire.Hosting/withBuilderParentRelationship", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Sets a child relationship
    pub fn with_child_relationship(&self, child: &IResource) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("child".to_string(), child.handle().to_json());
        let result = self.client.invoke_capability("Aspire.Hosting/withBuilderChildRelationship", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Sets the icon for the resource
    pub fn with_icon_name(&self, icon_name: &str, icon_variant: Option<IconVariant>) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("iconName".to_string(), serde_json::to_value(&icon_name).unwrap_or(Value::Null));
        if let Some(ref v) = icon_variant {
            args.insert("iconVariant".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        let result = self.client.invoke_capability("Aspire.Hosting/withIconName", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Adds an HTTP health probe to the resource
    pub fn with_http_probe(&self, probe_type: ProbeType, path: Option<&str>, initial_delay_seconds: Option<f64>, period_seconds: Option<f64>, timeout_seconds: Option<f64>, failure_threshold: Option<f64>, success_threshold: Option<f64>, endpoint_name: Option<&str>) -> Result<IResourceWithEndpoints, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("probeType".to_string(), serde_json::to_value(&probe_type).unwrap_or(Value::Null));
        if let Some(ref v) = path {
            args.insert("path".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        if let Some(ref v) = initial_delay_seconds {
            args.insert("initialDelaySeconds".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        if let Some(ref v) = period_seconds {
            args.insert("periodSeconds".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        if let Some(ref v) = timeout_seconds {
            args.insert("timeoutSeconds".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        if let Some(ref v) = failure_threshold {
            args.insert("failureThreshold".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        if let Some(ref v) = success_threshold {
            args.insert("successThreshold".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        if let Some(ref v) = endpoint_name {
            args.insert("endpointName".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        let result = self.client.invoke_capability("Aspire.Hosting/withHttpProbe", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResourceWithEndpoints::new(handle, self.client.clone()))
    }

    /// Excludes the resource from MCP server exposure
    pub fn exclude_from_mcp(&self) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        let result = self.client.invoke_capability("Aspire.Hosting/excludeFromMcp", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Sets the remote image name for publishing
    pub fn with_remote_image_name(&self, remote_image_name: &str) -> Result<IComputeResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("remoteImageName".to_string(), serde_json::to_value(&remote_image_name).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting/withRemoteImageName", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IComputeResource::new(handle, self.client.clone()))
    }

    /// Sets the remote image tag for publishing
    pub fn with_remote_image_tag(&self, remote_image_tag: &str) -> Result<IComputeResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("remoteImageTag".to_string(), serde_json::to_value(&remote_image_tag).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting/withRemoteImageTag", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IComputeResource::new(handle, self.client.clone()))
    }

    /// Adds a pipeline step to the resource
    pub fn with_pipeline_step_factory(&self, step_name: &str, callback: impl Fn(Vec<Value>) -> Value + Send + Sync + 'static, depends_on: Option<Vec<String>>, required_by: Option<Vec<String>>, tags: Option<Vec<String>>, description: Option<&str>) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("stepName".to_string(), serde_json::to_value(&step_name).unwrap_or(Value::Null));
        let callback_id = register_callback(callback);
        args.insert("callback".to_string(), Value::String(callback_id));
        if let Some(ref v) = depends_on {
            args.insert("dependsOn".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        if let Some(ref v) = required_by {
            args.insert("requiredBy".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        if let Some(ref v) = tags {
            args.insert("tags".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        if let Some(ref v) = description {
            args.insert("description".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        let result = self.client.invoke_capability("Aspire.Hosting/withPipelineStepFactory", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Configures pipeline step dependencies via a callback
    pub fn with_pipeline_configuration(&self, callback: impl Fn(Vec<Value>) -> Value + Send + Sync + 'static) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        let callback_id = register_callback(callback);
        args.insert("callback".to_string(), Value::String(callback_id));
        let result = self.client.invoke_capability("Aspire.Hosting/withPipelineConfiguration", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Adds a volume
    pub fn with_volume(&self, target: &str, name: Option<&str>, is_read_only: Option<bool>) -> Result<ContainerResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("resource".to_string(), self.handle.to_json());
        args.insert("target".to_string(), serde_json::to_value(&target).unwrap_or(Value::Null));
        if let Some(ref v) = name {
            args.insert("name".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        if let Some(ref v) = is_read_only {
            args.insert("isReadOnly".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        let result = self.client.invoke_capability("Aspire.Hosting/withVolume", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(ContainerResource::new(handle, self.client.clone()))
    }

    /// Gets the resource name
    pub fn get_resource_name(&self) -> Result<String, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("resource".to_string(), self.handle.to_json());
        let result = self.client.invoke_capability("Aspire.Hosting/getResourceName", args)?;
        Ok(serde_json::from_value(result)?)
    }

    /// Subscribes to the BeforeResourceStarted event
    pub fn on_before_resource_started(&self, callback: impl Fn(Vec<Value>) -> Value + Send + Sync + 'static) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        let callback_id = register_callback(callback);
        args.insert("callback".to_string(), Value::String(callback_id));
        let result = self.client.invoke_capability("Aspire.Hosting/onBeforeResourceStarted", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Subscribes to the ResourceStopped event
    pub fn on_resource_stopped(&self, callback: impl Fn(Vec<Value>) -> Value + Send + Sync + 'static) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        let callback_id = register_callback(callback);
        args.insert("callback".to_string(), Value::String(callback_id));
        let result = self.client.invoke_capability("Aspire.Hosting/onResourceStopped", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Subscribes to the InitializeResource event
    pub fn on_initialize_resource(&self, callback: impl Fn(Vec<Value>) -> Value + Send + Sync + 'static) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        let callback_id = register_callback(callback);
        args.insert("callback".to_string(), Value::String(callback_id));
        let result = self.client.invoke_capability("Aspire.Hosting/onInitializeResource", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Subscribes to the ResourceEndpointsAllocated event
    pub fn on_resource_endpoints_allocated(&self, callback: impl Fn(Vec<Value>) -> Value + Send + Sync + 'static) -> Result<IResourceWithEndpoints, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        let callback_id = register_callback(callback);
        args.insert("callback".to_string(), Value::String(callback_id));
        let result = self.client.invoke_capability("Aspire.Hosting/onResourceEndpointsAllocated", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResourceWithEndpoints::new(handle, self.client.clone()))
    }

    /// Subscribes to the ResourceReady event
    pub fn on_resource_ready(&self, callback: impl Fn(Vec<Value>) -> Value + Send + Sync + 'static) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        let callback_id = register_callback(callback);
        args.insert("callback".to_string(), Value::String(callback_id));
        let result = self.client.invoke_capability("Aspire.Hosting/onResourceReady", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
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

    /// Adds a dependency from a string or another resource
    pub fn with_union_dependency(&self, dependency: Value) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("dependency".to_string(), serde_json::to_value(&dependency).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting.CodeGeneration.Rust.Tests/withUnionDependency", args)?;
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

    /// Adds a label to the resource
    pub fn with_merge_label(&self, label: &str) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("label".to_string(), serde_json::to_value(&label).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting.CodeGeneration.Rust.Tests/withMergeLabel", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Adds a categorized label to the resource
    pub fn with_merge_label_categorized(&self, label: &str, category: &str) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("label".to_string(), serde_json::to_value(&label).unwrap_or(Value::Null));
        args.insert("category".to_string(), serde_json::to_value(&category).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting.CodeGeneration.Rust.Tests/withMergeLabelCategorized", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Configures a named endpoint
    pub fn with_merge_endpoint(&self, endpoint_name: &str, port: f64) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("endpointName".to_string(), serde_json::to_value(&endpoint_name).unwrap_or(Value::Null));
        args.insert("port".to_string(), serde_json::to_value(&port).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting.CodeGeneration.Rust.Tests/withMergeEndpoint", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Configures a named endpoint with scheme
    pub fn with_merge_endpoint_scheme(&self, endpoint_name: &str, port: f64, scheme: &str) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("endpointName".to_string(), serde_json::to_value(&endpoint_name).unwrap_or(Value::Null));
        args.insert("port".to_string(), serde_json::to_value(&port).unwrap_or(Value::Null));
        args.insert("scheme".to_string(), serde_json::to_value(&scheme).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting.CodeGeneration.Rust.Tests/withMergeEndpointScheme", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Configures resource logging
    pub fn with_merge_logging(&self, log_level: &str, enable_console: Option<bool>, max_files: Option<f64>) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("logLevel".to_string(), serde_json::to_value(&log_level).unwrap_or(Value::Null));
        if let Some(ref v) = enable_console {
            args.insert("enableConsole".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        if let Some(ref v) = max_files {
            args.insert("maxFiles".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        let result = self.client.invoke_capability("Aspire.Hosting.CodeGeneration.Rust.Tests/withMergeLogging", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Configures resource logging with file path
    pub fn with_merge_logging_path(&self, log_level: &str, log_path: &str, enable_console: Option<bool>, max_files: Option<f64>) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("logLevel".to_string(), serde_json::to_value(&log_level).unwrap_or(Value::Null));
        args.insert("logPath".to_string(), serde_json::to_value(&log_path).unwrap_or(Value::Null));
        if let Some(ref v) = enable_console {
            args.insert("enableConsole".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        if let Some(ref v) = max_files {
            args.insert("maxFiles".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        let result = self.client.invoke_capability("Aspire.Hosting.CodeGeneration.Rust.Tests/withMergeLoggingPath", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Configures a route
    pub fn with_merge_route(&self, path: &str, method: &str, handler: &str, priority: f64) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("path".to_string(), serde_json::to_value(&path).unwrap_or(Value::Null));
        args.insert("method".to_string(), serde_json::to_value(&method).unwrap_or(Value::Null));
        args.insert("handler".to_string(), serde_json::to_value(&handler).unwrap_or(Value::Null));
        args.insert("priority".to_string(), serde_json::to_value(&priority).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting.CodeGeneration.Rust.Tests/withMergeRoute", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Configures a route with middleware
    pub fn with_merge_route_middleware(&self, path: &str, method: &str, handler: &str, priority: f64, middleware: &str) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("path".to_string(), serde_json::to_value(&path).unwrap_or(Value::Null));
        args.insert("method".to_string(), serde_json::to_value(&method).unwrap_or(Value::Null));
        args.insert("handler".to_string(), serde_json::to_value(&handler).unwrap_or(Value::Null));
        args.insert("priority".to_string(), serde_json::to_value(&priority).unwrap_or(Value::Null));
        args.insert("middleware".to_string(), serde_json::to_value(&middleware).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting.CodeGeneration.Rust.Tests/withMergeRouteMiddleware", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
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

    /// Configures a resource to use a container registry
    pub fn with_container_registry(&self, registry: &IResource) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("registry".to_string(), registry.handle().to_json());
        let result = self.client.invoke_capability("Aspire.Hosting/withContainerRegistry", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Adds a bind mount
    pub fn with_bind_mount(&self, source: &str, target: &str, is_read_only: Option<bool>) -> Result<ContainerResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("source".to_string(), serde_json::to_value(&source).unwrap_or(Value::Null));
        args.insert("target".to_string(), serde_json::to_value(&target).unwrap_or(Value::Null));
        if let Some(ref v) = is_read_only {
            args.insert("isReadOnly".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        let result = self.client.invoke_capability("Aspire.Hosting/withBindMount", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(ContainerResource::new(handle, self.client.clone()))
    }

    /// Sets the container entrypoint
    pub fn with_entrypoint(&self, entrypoint: &str) -> Result<ContainerResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("entrypoint".to_string(), serde_json::to_value(&entrypoint).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting/withEntrypoint", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(ContainerResource::new(handle, self.client.clone()))
    }

    /// Sets the container image tag
    pub fn with_image_tag(&self, tag: &str) -> Result<ContainerResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("tag".to_string(), serde_json::to_value(&tag).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting/withImageTag", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(ContainerResource::new(handle, self.client.clone()))
    }

    /// Sets the container image registry
    pub fn with_image_registry(&self, registry: &str) -> Result<ContainerResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("registry".to_string(), serde_json::to_value(&registry).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting/withImageRegistry", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(ContainerResource::new(handle, self.client.clone()))
    }

    /// Sets the container image
    pub fn with_image(&self, image: &str, tag: Option<&str>) -> Result<ContainerResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("image".to_string(), serde_json::to_value(&image).unwrap_or(Value::Null));
        if let Some(ref v) = tag {
            args.insert("tag".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        let result = self.client.invoke_capability("Aspire.Hosting/withImage", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(ContainerResource::new(handle, self.client.clone()))
    }

    /// Sets the image SHA256 digest
    pub fn with_image_sha256(&self, sha256: &str) -> Result<ContainerResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("sha256".to_string(), serde_json::to_value(&sha256).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting/withImageSHA256", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(ContainerResource::new(handle, self.client.clone()))
    }

    /// Adds runtime arguments for the container
    pub fn with_container_runtime_args(&self, args: Vec<String>) -> Result<ContainerResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("args".to_string(), serde_json::to_value(&args).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting/withContainerRuntimeArgs", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(ContainerResource::new(handle, self.client.clone()))
    }

    /// Sets the lifetime behavior of the container resource
    pub fn with_lifetime(&self, lifetime: ContainerLifetime) -> Result<ContainerResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("lifetime".to_string(), serde_json::to_value(&lifetime).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting/withLifetime", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(ContainerResource::new(handle, self.client.clone()))
    }

    /// Sets the container image pull policy
    pub fn with_image_pull_policy(&self, pull_policy: ImagePullPolicy) -> Result<ContainerResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("pullPolicy".to_string(), serde_json::to_value(&pull_policy).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting/withImagePullPolicy", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(ContainerResource::new(handle, self.client.clone()))
    }

    /// Configures the resource to be published as a container
    pub fn publish_as_container(&self) -> Result<ContainerResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        let result = self.client.invoke_capability("Aspire.Hosting/publishAsContainer", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(ContainerResource::new(handle, self.client.clone()))
    }

    /// Configures the resource to use a Dockerfile
    pub fn with_dockerfile(&self, context_path: &str, dockerfile_path: Option<&str>, stage: Option<&str>) -> Result<ContainerResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("contextPath".to_string(), serde_json::to_value(&context_path).unwrap_or(Value::Null));
        if let Some(ref v) = dockerfile_path {
            args.insert("dockerfilePath".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        if let Some(ref v) = stage {
            args.insert("stage".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        let result = self.client.invoke_capability("Aspire.Hosting/withDockerfile", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(ContainerResource::new(handle, self.client.clone()))
    }

    /// Sets the container name
    pub fn with_container_name(&self, name: &str) -> Result<ContainerResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("name".to_string(), serde_json::to_value(&name).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting/withContainerName", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(ContainerResource::new(handle, self.client.clone()))
    }

    /// Adds a build argument from a parameter resource
    pub fn with_build_arg(&self, name: &str, value: &ParameterResource) -> Result<ContainerResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("name".to_string(), serde_json::to_value(&name).unwrap_or(Value::Null));
        args.insert("value".to_string(), value.handle().to_json());
        let result = self.client.invoke_capability("Aspire.Hosting/withParameterBuildArg", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(ContainerResource::new(handle, self.client.clone()))
    }

    /// Adds a build secret from a parameter resource
    pub fn with_build_secret(&self, name: &str, value: &ParameterResource) -> Result<ContainerResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("name".to_string(), serde_json::to_value(&name).unwrap_or(Value::Null));
        args.insert("value".to_string(), value.handle().to_json());
        let result = self.client.invoke_capability("Aspire.Hosting/withParameterBuildSecret", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(ContainerResource::new(handle, self.client.clone()))
    }

    /// Configures endpoint proxy support
    pub fn with_endpoint_proxy_support(&self, proxy_enabled: bool) -> Result<ContainerResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("proxyEnabled".to_string(), serde_json::to_value(&proxy_enabled).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting/withEndpointProxySupport", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(ContainerResource::new(handle, self.client.clone()))
    }

    /// Sets the base image for a Dockerfile build
    pub fn with_dockerfile_base_image(&self, build_image: Option<&str>, runtime_image: Option<&str>) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        if let Some(ref v) = build_image {
            args.insert("buildImage".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        if let Some(ref v) = runtime_image {
            args.insert("runtimeImage".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        let result = self.client.invoke_capability("Aspire.Hosting/withDockerfileBaseImage", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Adds a network alias for the container
    pub fn with_container_network_alias(&self, alias: &str) -> Result<ContainerResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("alias".to_string(), serde_json::to_value(&alias).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting/withContainerNetworkAlias", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(ContainerResource::new(handle, self.client.clone()))
    }

    /// Configures an MCP server endpoint on the resource
    pub fn with_mcp_server(&self, path: Option<&str>, endpoint_name: Option<&str>) -> Result<IResourceWithEndpoints, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        if let Some(ref v) = path {
            args.insert("path".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        if let Some(ref v) = endpoint_name {
            args.insert("endpointName".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        let result = self.client.invoke_capability("Aspire.Hosting/withMcpServer", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResourceWithEndpoints::new(handle, self.client.clone()))
    }

    /// Configures OTLP telemetry export
    pub fn with_otlp_exporter(&self) -> Result<IResourceWithEnvironment, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        let result = self.client.invoke_capability("Aspire.Hosting/withOtlpExporter", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResourceWithEnvironment::new(handle, self.client.clone()))
    }

    /// Configures OTLP telemetry export with specific protocol
    pub fn with_otlp_exporter_protocol(&self, protocol: OtlpProtocol) -> Result<IResourceWithEnvironment, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("protocol".to_string(), serde_json::to_value(&protocol).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting/withOtlpExporterProtocol", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResourceWithEnvironment::new(handle, self.client.clone()))
    }

    /// Publishes the resource as a connection string
    pub fn publish_as_connection_string(&self) -> Result<ContainerResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        let result = self.client.invoke_capability("Aspire.Hosting/publishAsConnectionString", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(ContainerResource::new(handle, self.client.clone()))
    }

    /// Adds a required command dependency
    pub fn with_required_command(&self, command: &str, help_link: Option<&str>) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("command".to_string(), serde_json::to_value(&command).unwrap_or(Value::Null));
        if let Some(ref v) = help_link {
            args.insert("helpLink".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        let result = self.client.invoke_capability("Aspire.Hosting/withRequiredCommand", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Sets an environment variable
    pub fn with_environment(&self, name: &str, value: Value) -> Result<IResourceWithEnvironment, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("name".to_string(), serde_json::to_value(&name).unwrap_or(Value::Null));
        args.insert("value".to_string(), serde_json::to_value(&value).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting/withEnvironment", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResourceWithEnvironment::new(handle, self.client.clone()))
    }

    /// Sets an environment variable from a reference expression
    pub fn with_environment_expression(&self, name: &str, value: ReferenceExpression) -> Result<IResourceWithEnvironment, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("name".to_string(), serde_json::to_value(&name).unwrap_or(Value::Null));
        args.insert("value".to_string(), serde_json::to_value(&value).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting/withEnvironmentExpression", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResourceWithEnvironment::new(handle, self.client.clone()))
    }

    /// Sets environment variables via callback
    pub fn with_environment_callback(&self, callback: impl Fn(Vec<Value>) -> Value + Send + Sync + 'static) -> Result<IResourceWithEnvironment, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        let callback_id = register_callback(callback);
        args.insert("callback".to_string(), Value::String(callback_id));
        let result = self.client.invoke_capability("Aspire.Hosting/withEnvironmentCallback", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResourceWithEnvironment::new(handle, self.client.clone()))
    }

    /// Sets an environment variable from an endpoint reference
    pub fn with_environment_endpoint(&self, name: &str, endpoint_reference: &EndpointReference) -> Result<IResourceWithEnvironment, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("name".to_string(), serde_json::to_value(&name).unwrap_or(Value::Null));
        args.insert("endpointReference".to_string(), endpoint_reference.handle().to_json());
        let result = self.client.invoke_capability("Aspire.Hosting/withEnvironmentEndpoint", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResourceWithEnvironment::new(handle, self.client.clone()))
    }

    /// Sets an environment variable from a parameter resource
    pub fn with_environment_parameter(&self, name: &str, parameter: &ParameterResource) -> Result<IResourceWithEnvironment, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("name".to_string(), serde_json::to_value(&name).unwrap_or(Value::Null));
        args.insert("parameter".to_string(), parameter.handle().to_json());
        let result = self.client.invoke_capability("Aspire.Hosting/withEnvironmentParameter", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResourceWithEnvironment::new(handle, self.client.clone()))
    }

    /// Sets an environment variable from a connection string resource
    pub fn with_environment_connection_string(&self, env_var_name: &str, resource: &IResourceWithConnectionString) -> Result<IResourceWithEnvironment, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("envVarName".to_string(), serde_json::to_value(&env_var_name).unwrap_or(Value::Null));
        args.insert("resource".to_string(), resource.handle().to_json());
        let result = self.client.invoke_capability("Aspire.Hosting/withEnvironmentConnectionString", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResourceWithEnvironment::new(handle, self.client.clone()))
    }

    /// Adds a connection property with a reference expression
    pub fn with_connection_property(&self, name: &str, value: ReferenceExpression) -> Result<IResourceWithConnectionString, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("name".to_string(), serde_json::to_value(&name).unwrap_or(Value::Null));
        args.insert("value".to_string(), serde_json::to_value(&value).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting/withConnectionProperty", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResourceWithConnectionString::new(handle, self.client.clone()))
    }

    /// Adds a connection property with a string value
    pub fn with_connection_property_value(&self, name: &str, value: &str) -> Result<IResourceWithConnectionString, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("name".to_string(), serde_json::to_value(&name).unwrap_or(Value::Null));
        args.insert("value".to_string(), serde_json::to_value(&value).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting/withConnectionPropertyValue", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResourceWithConnectionString::new(handle, self.client.clone()))
    }

    /// Adds arguments
    pub fn with_args(&self, args: Vec<String>) -> Result<IResourceWithArgs, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("args".to_string(), serde_json::to_value(&args).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting/withArgs", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResourceWithArgs::new(handle, self.client.clone()))
    }

    /// Sets command-line arguments via callback
    pub fn with_args_callback(&self, callback: impl Fn(Vec<Value>) -> Value + Send + Sync + 'static) -> Result<IResourceWithArgs, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        let callback_id = register_callback(callback);
        args.insert("callback".to_string(), Value::String(callback_id));
        let result = self.client.invoke_capability("Aspire.Hosting/withArgsCallback", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResourceWithArgs::new(handle, self.client.clone()))
    }

    /// Adds a reference to another resource
    pub fn with_reference(&self, source: &IResource, connection_name: Option<&str>, optional: Option<bool>, name: Option<&str>) -> Result<IResourceWithEnvironment, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("source".to_string(), source.handle().to_json());
        if let Some(ref v) = connection_name {
            args.insert("connectionName".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        if let Some(ref v) = optional {
            args.insert("optional".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        if let Some(ref v) = name {
            args.insert("name".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        let result = self.client.invoke_capability("Aspire.Hosting/withReference", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResourceWithEnvironment::new(handle, self.client.clone()))
    }

    /// Gets a connection property by key
    pub fn get_connection_property(&self, key: &str) -> Result<ReferenceExpression, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("resource".to_string(), self.handle.to_json());
        args.insert("key".to_string(), serde_json::to_value(&key).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting/getConnectionProperty", args)?;
        Ok(serde_json::from_value(result)?)
    }

    /// Adds a reference to a URI
    pub fn with_reference_uri(&self, name: &str, uri: &str) -> Result<IResourceWithEnvironment, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("name".to_string(), serde_json::to_value(&name).unwrap_or(Value::Null));
        args.insert("uri".to_string(), serde_json::to_value(&uri).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting/withReferenceUri", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResourceWithEnvironment::new(handle, self.client.clone()))
    }

    /// Adds a reference to an external service
    pub fn with_reference_external_service(&self, external_service: &ExternalServiceResource) -> Result<IResourceWithEnvironment, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("externalService".to_string(), external_service.handle().to_json());
        let result = self.client.invoke_capability("Aspire.Hosting/withReferenceExternalService", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResourceWithEnvironment::new(handle, self.client.clone()))
    }

    /// Adds a reference to an endpoint
    pub fn with_reference_endpoint(&self, endpoint_reference: &EndpointReference) -> Result<IResourceWithEnvironment, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("endpointReference".to_string(), endpoint_reference.handle().to_json());
        let result = self.client.invoke_capability("Aspire.Hosting/withReferenceEndpoint", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResourceWithEnvironment::new(handle, self.client.clone()))
    }

    /// Adds a network endpoint
    pub fn with_endpoint(&self, port: Option<f64>, target_port: Option<f64>, scheme: Option<&str>, name: Option<&str>, env: Option<&str>, is_proxied: Option<bool>, is_external: Option<bool>, protocol: Option<ProtocolType>) -> Result<IResourceWithEndpoints, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        if let Some(ref v) = port {
            args.insert("port".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        if let Some(ref v) = target_port {
            args.insert("targetPort".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        if let Some(ref v) = scheme {
            args.insert("scheme".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        if let Some(ref v) = name {
            args.insert("name".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        if let Some(ref v) = env {
            args.insert("env".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        if let Some(ref v) = is_proxied {
            args.insert("isProxied".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        if let Some(ref v) = is_external {
            args.insert("isExternal".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        if let Some(ref v) = protocol {
            args.insert("protocol".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        let result = self.client.invoke_capability("Aspire.Hosting/withEndpoint", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResourceWithEndpoints::new(handle, self.client.clone()))
    }

    /// Adds an HTTP endpoint
    pub fn with_http_endpoint(&self, port: Option<f64>, target_port: Option<f64>, name: Option<&str>, env: Option<&str>, is_proxied: Option<bool>) -> Result<IResourceWithEndpoints, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        if let Some(ref v) = port {
            args.insert("port".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        if let Some(ref v) = target_port {
            args.insert("targetPort".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        if let Some(ref v) = name {
            args.insert("name".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        if let Some(ref v) = env {
            args.insert("env".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        if let Some(ref v) = is_proxied {
            args.insert("isProxied".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        let result = self.client.invoke_capability("Aspire.Hosting/withHttpEndpoint", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResourceWithEndpoints::new(handle, self.client.clone()))
    }

    /// Adds an HTTPS endpoint
    pub fn with_https_endpoint(&self, port: Option<f64>, target_port: Option<f64>, name: Option<&str>, env: Option<&str>, is_proxied: Option<bool>) -> Result<IResourceWithEndpoints, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        if let Some(ref v) = port {
            args.insert("port".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        if let Some(ref v) = target_port {
            args.insert("targetPort".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        if let Some(ref v) = name {
            args.insert("name".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        if let Some(ref v) = env {
            args.insert("env".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        if let Some(ref v) = is_proxied {
            args.insert("isProxied".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        let result = self.client.invoke_capability("Aspire.Hosting/withHttpsEndpoint", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResourceWithEndpoints::new(handle, self.client.clone()))
    }

    /// Makes HTTP endpoints externally accessible
    pub fn with_external_http_endpoints(&self) -> Result<IResourceWithEndpoints, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        let result = self.client.invoke_capability("Aspire.Hosting/withExternalHttpEndpoints", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResourceWithEndpoints::new(handle, self.client.clone()))
    }

    /// Gets an endpoint reference
    pub fn get_endpoint(&self, name: &str) -> Result<EndpointReference, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("name".to_string(), serde_json::to_value(&name).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting/getEndpoint", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(EndpointReference::new(handle, self.client.clone()))
    }

    /// Configures resource for HTTP/2
    pub fn as_http2_service(&self) -> Result<IResourceWithEndpoints, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        let result = self.client.invoke_capability("Aspire.Hosting/asHttp2Service", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResourceWithEndpoints::new(handle, self.client.clone()))
    }

    /// Customizes displayed URLs via callback
    pub fn with_urls_callback(&self, callback: impl Fn(Vec<Value>) -> Value + Send + Sync + 'static) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        let callback_id = register_callback(callback);
        args.insert("callback".to_string(), Value::String(callback_id));
        let result = self.client.invoke_capability("Aspire.Hosting/withUrlsCallback", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Adds or modifies displayed URLs
    pub fn with_url(&self, url: &str, display_text: Option<&str>) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("url".to_string(), serde_json::to_value(&url).unwrap_or(Value::Null));
        if let Some(ref v) = display_text {
            args.insert("displayText".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        let result = self.client.invoke_capability("Aspire.Hosting/withUrl", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Adds a URL using a reference expression
    pub fn with_url_expression(&self, url: ReferenceExpression, display_text: Option<&str>) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("url".to_string(), serde_json::to_value(&url).unwrap_or(Value::Null));
        if let Some(ref v) = display_text {
            args.insert("displayText".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        let result = self.client.invoke_capability("Aspire.Hosting/withUrlExpression", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Customizes the URL for a specific endpoint via callback
    pub fn with_url_for_endpoint(&self, endpoint_name: &str, callback: impl Fn(Vec<Value>) -> Value + Send + Sync + 'static) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("endpointName".to_string(), serde_json::to_value(&endpoint_name).unwrap_or(Value::Null));
        let callback_id = register_callback(callback);
        args.insert("callback".to_string(), Value::String(callback_id));
        let result = self.client.invoke_capability("Aspire.Hosting/withUrlForEndpoint", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Adds a URL for a specific endpoint via factory callback
    pub fn with_url_for_endpoint_factory(&self, endpoint_name: &str, callback: impl Fn(Vec<Value>) -> Value + Send + Sync + 'static) -> Result<IResourceWithEndpoints, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("endpointName".to_string(), serde_json::to_value(&endpoint_name).unwrap_or(Value::Null));
        let callback_id = register_callback(callback);
        args.insert("callback".to_string(), Value::String(callback_id));
        let result = self.client.invoke_capability("Aspire.Hosting/withUrlForEndpointFactory", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResourceWithEndpoints::new(handle, self.client.clone()))
    }

    /// Excludes the resource from the deployment manifest
    pub fn exclude_from_manifest(&self) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        let result = self.client.invoke_capability("Aspire.Hosting/excludeFromManifest", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Waits for another resource to be ready
    pub fn wait_for(&self, dependency: &IResource) -> Result<IResourceWithWaitSupport, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("dependency".to_string(), dependency.handle().to_json());
        let result = self.client.invoke_capability("Aspire.Hosting/waitForResource", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResourceWithWaitSupport::new(handle, self.client.clone()))
    }

    /// Waits for another resource with specific behavior
    pub fn wait_for_with_behavior(&self, dependency: &IResource, wait_behavior: WaitBehavior) -> Result<IResourceWithWaitSupport, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("dependency".to_string(), dependency.handle().to_json());
        args.insert("waitBehavior".to_string(), serde_json::to_value(&wait_behavior).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting/waitForWithBehavior", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResourceWithWaitSupport::new(handle, self.client.clone()))
    }

    /// Waits for another resource to start
    pub fn wait_for_start(&self, dependency: &IResource) -> Result<IResourceWithWaitSupport, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("dependency".to_string(), dependency.handle().to_json());
        let result = self.client.invoke_capability("Aspire.Hosting/waitForResourceStart", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResourceWithWaitSupport::new(handle, self.client.clone()))
    }

    /// Waits for another resource to start with specific behavior
    pub fn wait_for_start_with_behavior(&self, dependency: &IResource, wait_behavior: WaitBehavior) -> Result<IResourceWithWaitSupport, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("dependency".to_string(), dependency.handle().to_json());
        args.insert("waitBehavior".to_string(), serde_json::to_value(&wait_behavior).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting/waitForStartWithBehavior", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResourceWithWaitSupport::new(handle, self.client.clone()))
    }

    /// Prevents resource from starting automatically
    pub fn with_explicit_start(&self) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        let result = self.client.invoke_capability("Aspire.Hosting/withExplicitStart", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Waits for resource completion
    pub fn wait_for_completion(&self, dependency: &IResource, exit_code: Option<f64>) -> Result<IResourceWithWaitSupport, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("dependency".to_string(), dependency.handle().to_json());
        if let Some(ref v) = exit_code {
            args.insert("exitCode".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        let result = self.client.invoke_capability("Aspire.Hosting/waitForResourceCompletion", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResourceWithWaitSupport::new(handle, self.client.clone()))
    }

    /// Adds a health check by key
    pub fn with_health_check(&self, key: &str) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("key".to_string(), serde_json::to_value(&key).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting/withHealthCheck", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Adds an HTTP health check
    pub fn with_http_health_check(&self, path: Option<&str>, status_code: Option<f64>, endpoint_name: Option<&str>) -> Result<IResourceWithEndpoints, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        if let Some(ref v) = path {
            args.insert("path".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        if let Some(ref v) = status_code {
            args.insert("statusCode".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        if let Some(ref v) = endpoint_name {
            args.insert("endpointName".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        let result = self.client.invoke_capability("Aspire.Hosting/withHttpHealthCheck", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResourceWithEndpoints::new(handle, self.client.clone()))
    }

    /// Adds a resource command
    pub fn with_command(&self, name: &str, display_name: &str, execute_command: impl Fn(Vec<Value>) -> Value + Send + Sync + 'static, command_options: Option<CommandOptions>) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("name".to_string(), serde_json::to_value(&name).unwrap_or(Value::Null));
        args.insert("displayName".to_string(), serde_json::to_value(&display_name).unwrap_or(Value::Null));
        let callback_id = register_callback(execute_command);
        args.insert("executeCommand".to_string(), Value::String(callback_id));
        if let Some(ref v) = command_options {
            args.insert("commandOptions".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        let result = self.client.invoke_capability("Aspire.Hosting/withCommand", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Configures developer certificate trust
    pub fn with_developer_certificate_trust(&self, trust: bool) -> Result<IResourceWithEnvironment, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("trust".to_string(), serde_json::to_value(&trust).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting/withDeveloperCertificateTrust", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResourceWithEnvironment::new(handle, self.client.clone()))
    }

    /// Sets the certificate trust scope
    pub fn with_certificate_trust_scope(&self, scope: CertificateTrustScope) -> Result<IResourceWithEnvironment, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("scope".to_string(), serde_json::to_value(&scope).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting/withCertificateTrustScope", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResourceWithEnvironment::new(handle, self.client.clone()))
    }

    /// Configures HTTPS with a developer certificate
    pub fn with_https_developer_certificate(&self, password: Option<&ParameterResource>) -> Result<IResourceWithEnvironment, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        if let Some(ref v) = password {
            args.insert("password".to_string(), v.handle().to_json());
        }
        let result = self.client.invoke_capability("Aspire.Hosting/withParameterHttpsDeveloperCertificate", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResourceWithEnvironment::new(handle, self.client.clone()))
    }

    /// Removes HTTPS certificate configuration
    pub fn without_https_certificate(&self) -> Result<IResourceWithEnvironment, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        let result = self.client.invoke_capability("Aspire.Hosting/withoutHttpsCertificate", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResourceWithEnvironment::new(handle, self.client.clone()))
    }

    /// Sets the parent relationship
    pub fn with_parent_relationship(&self, parent: &IResource) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("parent".to_string(), parent.handle().to_json());
        let result = self.client.invoke_capability("Aspire.Hosting/withBuilderParentRelationship", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Sets a child relationship
    pub fn with_child_relationship(&self, child: &IResource) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("child".to_string(), child.handle().to_json());
        let result = self.client.invoke_capability("Aspire.Hosting/withBuilderChildRelationship", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Sets the icon for the resource
    pub fn with_icon_name(&self, icon_name: &str, icon_variant: Option<IconVariant>) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("iconName".to_string(), serde_json::to_value(&icon_name).unwrap_or(Value::Null));
        if let Some(ref v) = icon_variant {
            args.insert("iconVariant".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        let result = self.client.invoke_capability("Aspire.Hosting/withIconName", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Adds an HTTP health probe to the resource
    pub fn with_http_probe(&self, probe_type: ProbeType, path: Option<&str>, initial_delay_seconds: Option<f64>, period_seconds: Option<f64>, timeout_seconds: Option<f64>, failure_threshold: Option<f64>, success_threshold: Option<f64>, endpoint_name: Option<&str>) -> Result<IResourceWithEndpoints, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("probeType".to_string(), serde_json::to_value(&probe_type).unwrap_or(Value::Null));
        if let Some(ref v) = path {
            args.insert("path".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        if let Some(ref v) = initial_delay_seconds {
            args.insert("initialDelaySeconds".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        if let Some(ref v) = period_seconds {
            args.insert("periodSeconds".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        if let Some(ref v) = timeout_seconds {
            args.insert("timeoutSeconds".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        if let Some(ref v) = failure_threshold {
            args.insert("failureThreshold".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        if let Some(ref v) = success_threshold {
            args.insert("successThreshold".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        if let Some(ref v) = endpoint_name {
            args.insert("endpointName".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        let result = self.client.invoke_capability("Aspire.Hosting/withHttpProbe", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResourceWithEndpoints::new(handle, self.client.clone()))
    }

    /// Excludes the resource from MCP server exposure
    pub fn exclude_from_mcp(&self) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        let result = self.client.invoke_capability("Aspire.Hosting/excludeFromMcp", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Sets the remote image name for publishing
    pub fn with_remote_image_name(&self, remote_image_name: &str) -> Result<IComputeResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("remoteImageName".to_string(), serde_json::to_value(&remote_image_name).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting/withRemoteImageName", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IComputeResource::new(handle, self.client.clone()))
    }

    /// Sets the remote image tag for publishing
    pub fn with_remote_image_tag(&self, remote_image_tag: &str) -> Result<IComputeResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("remoteImageTag".to_string(), serde_json::to_value(&remote_image_tag).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting/withRemoteImageTag", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IComputeResource::new(handle, self.client.clone()))
    }

    /// Adds a pipeline step to the resource
    pub fn with_pipeline_step_factory(&self, step_name: &str, callback: impl Fn(Vec<Value>) -> Value + Send + Sync + 'static, depends_on: Option<Vec<String>>, required_by: Option<Vec<String>>, tags: Option<Vec<String>>, description: Option<&str>) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("stepName".to_string(), serde_json::to_value(&step_name).unwrap_or(Value::Null));
        let callback_id = register_callback(callback);
        args.insert("callback".to_string(), Value::String(callback_id));
        if let Some(ref v) = depends_on {
            args.insert("dependsOn".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        if let Some(ref v) = required_by {
            args.insert("requiredBy".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        if let Some(ref v) = tags {
            args.insert("tags".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        if let Some(ref v) = description {
            args.insert("description".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        let result = self.client.invoke_capability("Aspire.Hosting/withPipelineStepFactory", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Configures pipeline step dependencies via a callback
    pub fn with_pipeline_configuration(&self, callback: impl Fn(Vec<Value>) -> Value + Send + Sync + 'static) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        let callback_id = register_callback(callback);
        args.insert("callback".to_string(), Value::String(callback_id));
        let result = self.client.invoke_capability("Aspire.Hosting/withPipelineConfiguration", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Adds a volume
    pub fn with_volume(&self, target: &str, name: Option<&str>, is_read_only: Option<bool>) -> Result<ContainerResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("resource".to_string(), self.handle.to_json());
        args.insert("target".to_string(), serde_json::to_value(&target).unwrap_or(Value::Null));
        if let Some(ref v) = name {
            args.insert("name".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        if let Some(ref v) = is_read_only {
            args.insert("isReadOnly".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        let result = self.client.invoke_capability("Aspire.Hosting/withVolume", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(ContainerResource::new(handle, self.client.clone()))
    }

    /// Gets the resource name
    pub fn get_resource_name(&self) -> Result<String, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("resource".to_string(), self.handle.to_json());
        let result = self.client.invoke_capability("Aspire.Hosting/getResourceName", args)?;
        Ok(serde_json::from_value(result)?)
    }

    /// Subscribes to the BeforeResourceStarted event
    pub fn on_before_resource_started(&self, callback: impl Fn(Vec<Value>) -> Value + Send + Sync + 'static) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        let callback_id = register_callback(callback);
        args.insert("callback".to_string(), Value::String(callback_id));
        let result = self.client.invoke_capability("Aspire.Hosting/onBeforeResourceStarted", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Subscribes to the ResourceStopped event
    pub fn on_resource_stopped(&self, callback: impl Fn(Vec<Value>) -> Value + Send + Sync + 'static) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        let callback_id = register_callback(callback);
        args.insert("callback".to_string(), Value::String(callback_id));
        let result = self.client.invoke_capability("Aspire.Hosting/onResourceStopped", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Subscribes to the ConnectionStringAvailable event
    pub fn on_connection_string_available(&self, callback: impl Fn(Vec<Value>) -> Value + Send + Sync + 'static) -> Result<IResourceWithConnectionString, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        let callback_id = register_callback(callback);
        args.insert("callback".to_string(), Value::String(callback_id));
        let result = self.client.invoke_capability("Aspire.Hosting/onConnectionStringAvailable", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResourceWithConnectionString::new(handle, self.client.clone()))
    }

    /// Subscribes to the InitializeResource event
    pub fn on_initialize_resource(&self, callback: impl Fn(Vec<Value>) -> Value + Send + Sync + 'static) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        let callback_id = register_callback(callback);
        args.insert("callback".to_string(), Value::String(callback_id));
        let result = self.client.invoke_capability("Aspire.Hosting/onInitializeResource", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Subscribes to the ResourceEndpointsAllocated event
    pub fn on_resource_endpoints_allocated(&self, callback: impl Fn(Vec<Value>) -> Value + Send + Sync + 'static) -> Result<IResourceWithEndpoints, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        let callback_id = register_callback(callback);
        args.insert("callback".to_string(), Value::String(callback_id));
        let result = self.client.invoke_capability("Aspire.Hosting/onResourceEndpointsAllocated", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResourceWithEndpoints::new(handle, self.client.clone()))
    }

    /// Subscribes to the ResourceReady event
    pub fn on_resource_ready(&self, callback: impl Fn(Vec<Value>) -> Value + Send + Sync + 'static) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        let callback_id = register_callback(callback);
        args.insert("callback".to_string(), Value::String(callback_id));
        let result = self.client.invoke_capability("Aspire.Hosting/onResourceReady", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
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

    /// Adds a dependency from a string or another resource
    pub fn with_union_dependency(&self, dependency: Value) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("dependency".to_string(), serde_json::to_value(&dependency).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting.CodeGeneration.Rust.Tests/withUnionDependency", args)?;
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

    /// Adds a label to the resource
    pub fn with_merge_label(&self, label: &str) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("label".to_string(), serde_json::to_value(&label).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting.CodeGeneration.Rust.Tests/withMergeLabel", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Adds a categorized label to the resource
    pub fn with_merge_label_categorized(&self, label: &str, category: &str) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("label".to_string(), serde_json::to_value(&label).unwrap_or(Value::Null));
        args.insert("category".to_string(), serde_json::to_value(&category).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting.CodeGeneration.Rust.Tests/withMergeLabelCategorized", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Configures a named endpoint
    pub fn with_merge_endpoint(&self, endpoint_name: &str, port: f64) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("endpointName".to_string(), serde_json::to_value(&endpoint_name).unwrap_or(Value::Null));
        args.insert("port".to_string(), serde_json::to_value(&port).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting.CodeGeneration.Rust.Tests/withMergeEndpoint", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Configures a named endpoint with scheme
    pub fn with_merge_endpoint_scheme(&self, endpoint_name: &str, port: f64, scheme: &str) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("endpointName".to_string(), serde_json::to_value(&endpoint_name).unwrap_or(Value::Null));
        args.insert("port".to_string(), serde_json::to_value(&port).unwrap_or(Value::Null));
        args.insert("scheme".to_string(), serde_json::to_value(&scheme).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting.CodeGeneration.Rust.Tests/withMergeEndpointScheme", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Configures resource logging
    pub fn with_merge_logging(&self, log_level: &str, enable_console: Option<bool>, max_files: Option<f64>) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("logLevel".to_string(), serde_json::to_value(&log_level).unwrap_or(Value::Null));
        if let Some(ref v) = enable_console {
            args.insert("enableConsole".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        if let Some(ref v) = max_files {
            args.insert("maxFiles".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        let result = self.client.invoke_capability("Aspire.Hosting.CodeGeneration.Rust.Tests/withMergeLogging", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Configures resource logging with file path
    pub fn with_merge_logging_path(&self, log_level: &str, log_path: &str, enable_console: Option<bool>, max_files: Option<f64>) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("logLevel".to_string(), serde_json::to_value(&log_level).unwrap_or(Value::Null));
        args.insert("logPath".to_string(), serde_json::to_value(&log_path).unwrap_or(Value::Null));
        if let Some(ref v) = enable_console {
            args.insert("enableConsole".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        if let Some(ref v) = max_files {
            args.insert("maxFiles".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        let result = self.client.invoke_capability("Aspire.Hosting.CodeGeneration.Rust.Tests/withMergeLoggingPath", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Configures a route
    pub fn with_merge_route(&self, path: &str, method: &str, handler: &str, priority: f64) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("path".to_string(), serde_json::to_value(&path).unwrap_or(Value::Null));
        args.insert("method".to_string(), serde_json::to_value(&method).unwrap_or(Value::Null));
        args.insert("handler".to_string(), serde_json::to_value(&handler).unwrap_or(Value::Null));
        args.insert("priority".to_string(), serde_json::to_value(&priority).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting.CodeGeneration.Rust.Tests/withMergeRoute", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Configures a route with middleware
    pub fn with_merge_route_middleware(&self, path: &str, method: &str, handler: &str, priority: f64, middleware: &str) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("path".to_string(), serde_json::to_value(&path).unwrap_or(Value::Null));
        args.insert("method".to_string(), serde_json::to_value(&method).unwrap_or(Value::Null));
        args.insert("handler".to_string(), serde_json::to_value(&handler).unwrap_or(Value::Null));
        args.insert("priority".to_string(), serde_json::to_value(&priority).unwrap_or(Value::Null));
        args.insert("middleware".to_string(), serde_json::to_value(&middleware).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting.CodeGeneration.Rust.Tests/withMergeRouteMiddleware", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
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

    /// Configures a resource to use a container registry
    pub fn with_container_registry(&self, registry: &IResource) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("registry".to_string(), registry.handle().to_json());
        let result = self.client.invoke_capability("Aspire.Hosting/withContainerRegistry", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Adds a bind mount
    pub fn with_bind_mount(&self, source: &str, target: &str, is_read_only: Option<bool>) -> Result<ContainerResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("source".to_string(), serde_json::to_value(&source).unwrap_or(Value::Null));
        args.insert("target".to_string(), serde_json::to_value(&target).unwrap_or(Value::Null));
        if let Some(ref v) = is_read_only {
            args.insert("isReadOnly".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        let result = self.client.invoke_capability("Aspire.Hosting/withBindMount", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(ContainerResource::new(handle, self.client.clone()))
    }

    /// Sets the container entrypoint
    pub fn with_entrypoint(&self, entrypoint: &str) -> Result<ContainerResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("entrypoint".to_string(), serde_json::to_value(&entrypoint).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting/withEntrypoint", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(ContainerResource::new(handle, self.client.clone()))
    }

    /// Sets the container image tag
    pub fn with_image_tag(&self, tag: &str) -> Result<ContainerResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("tag".to_string(), serde_json::to_value(&tag).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting/withImageTag", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(ContainerResource::new(handle, self.client.clone()))
    }

    /// Sets the container image registry
    pub fn with_image_registry(&self, registry: &str) -> Result<ContainerResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("registry".to_string(), serde_json::to_value(&registry).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting/withImageRegistry", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(ContainerResource::new(handle, self.client.clone()))
    }

    /// Sets the container image
    pub fn with_image(&self, image: &str, tag: Option<&str>) -> Result<ContainerResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("image".to_string(), serde_json::to_value(&image).unwrap_or(Value::Null));
        if let Some(ref v) = tag {
            args.insert("tag".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        let result = self.client.invoke_capability("Aspire.Hosting/withImage", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(ContainerResource::new(handle, self.client.clone()))
    }

    /// Sets the image SHA256 digest
    pub fn with_image_sha256(&self, sha256: &str) -> Result<ContainerResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("sha256".to_string(), serde_json::to_value(&sha256).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting/withImageSHA256", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(ContainerResource::new(handle, self.client.clone()))
    }

    /// Adds runtime arguments for the container
    pub fn with_container_runtime_args(&self, args: Vec<String>) -> Result<ContainerResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("args".to_string(), serde_json::to_value(&args).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting/withContainerRuntimeArgs", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(ContainerResource::new(handle, self.client.clone()))
    }

    /// Sets the lifetime behavior of the container resource
    pub fn with_lifetime(&self, lifetime: ContainerLifetime) -> Result<ContainerResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("lifetime".to_string(), serde_json::to_value(&lifetime).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting/withLifetime", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(ContainerResource::new(handle, self.client.clone()))
    }

    /// Sets the container image pull policy
    pub fn with_image_pull_policy(&self, pull_policy: ImagePullPolicy) -> Result<ContainerResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("pullPolicy".to_string(), serde_json::to_value(&pull_policy).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting/withImagePullPolicy", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(ContainerResource::new(handle, self.client.clone()))
    }

    /// Configures the resource to be published as a container
    pub fn publish_as_container(&self) -> Result<ContainerResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        let result = self.client.invoke_capability("Aspire.Hosting/publishAsContainer", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(ContainerResource::new(handle, self.client.clone()))
    }

    /// Configures the resource to use a Dockerfile
    pub fn with_dockerfile(&self, context_path: &str, dockerfile_path: Option<&str>, stage: Option<&str>) -> Result<ContainerResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("contextPath".to_string(), serde_json::to_value(&context_path).unwrap_or(Value::Null));
        if let Some(ref v) = dockerfile_path {
            args.insert("dockerfilePath".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        if let Some(ref v) = stage {
            args.insert("stage".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        let result = self.client.invoke_capability("Aspire.Hosting/withDockerfile", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(ContainerResource::new(handle, self.client.clone()))
    }

    /// Sets the container name
    pub fn with_container_name(&self, name: &str) -> Result<ContainerResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("name".to_string(), serde_json::to_value(&name).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting/withContainerName", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(ContainerResource::new(handle, self.client.clone()))
    }

    /// Adds a build argument from a parameter resource
    pub fn with_build_arg(&self, name: &str, value: &ParameterResource) -> Result<ContainerResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("name".to_string(), serde_json::to_value(&name).unwrap_or(Value::Null));
        args.insert("value".to_string(), value.handle().to_json());
        let result = self.client.invoke_capability("Aspire.Hosting/withParameterBuildArg", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(ContainerResource::new(handle, self.client.clone()))
    }

    /// Adds a build secret from a parameter resource
    pub fn with_build_secret(&self, name: &str, value: &ParameterResource) -> Result<ContainerResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("name".to_string(), serde_json::to_value(&name).unwrap_or(Value::Null));
        args.insert("value".to_string(), value.handle().to_json());
        let result = self.client.invoke_capability("Aspire.Hosting/withParameterBuildSecret", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(ContainerResource::new(handle, self.client.clone()))
    }

    /// Configures endpoint proxy support
    pub fn with_endpoint_proxy_support(&self, proxy_enabled: bool) -> Result<ContainerResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("proxyEnabled".to_string(), serde_json::to_value(&proxy_enabled).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting/withEndpointProxySupport", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(ContainerResource::new(handle, self.client.clone()))
    }

    /// Sets the base image for a Dockerfile build
    pub fn with_dockerfile_base_image(&self, build_image: Option<&str>, runtime_image: Option<&str>) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        if let Some(ref v) = build_image {
            args.insert("buildImage".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        if let Some(ref v) = runtime_image {
            args.insert("runtimeImage".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        let result = self.client.invoke_capability("Aspire.Hosting/withDockerfileBaseImage", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Adds a network alias for the container
    pub fn with_container_network_alias(&self, alias: &str) -> Result<ContainerResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("alias".to_string(), serde_json::to_value(&alias).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting/withContainerNetworkAlias", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(ContainerResource::new(handle, self.client.clone()))
    }

    /// Configures an MCP server endpoint on the resource
    pub fn with_mcp_server(&self, path: Option<&str>, endpoint_name: Option<&str>) -> Result<IResourceWithEndpoints, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        if let Some(ref v) = path {
            args.insert("path".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        if let Some(ref v) = endpoint_name {
            args.insert("endpointName".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        let result = self.client.invoke_capability("Aspire.Hosting/withMcpServer", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResourceWithEndpoints::new(handle, self.client.clone()))
    }

    /// Configures OTLP telemetry export
    pub fn with_otlp_exporter(&self) -> Result<IResourceWithEnvironment, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        let result = self.client.invoke_capability("Aspire.Hosting/withOtlpExporter", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResourceWithEnvironment::new(handle, self.client.clone()))
    }

    /// Configures OTLP telemetry export with specific protocol
    pub fn with_otlp_exporter_protocol(&self, protocol: OtlpProtocol) -> Result<IResourceWithEnvironment, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("protocol".to_string(), serde_json::to_value(&protocol).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting/withOtlpExporterProtocol", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResourceWithEnvironment::new(handle, self.client.clone()))
    }

    /// Publishes the resource as a connection string
    pub fn publish_as_connection_string(&self) -> Result<ContainerResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        let result = self.client.invoke_capability("Aspire.Hosting/publishAsConnectionString", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(ContainerResource::new(handle, self.client.clone()))
    }

    /// Adds a required command dependency
    pub fn with_required_command(&self, command: &str, help_link: Option<&str>) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("command".to_string(), serde_json::to_value(&command).unwrap_or(Value::Null));
        if let Some(ref v) = help_link {
            args.insert("helpLink".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        let result = self.client.invoke_capability("Aspire.Hosting/withRequiredCommand", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Sets an environment variable
    pub fn with_environment(&self, name: &str, value: Value) -> Result<IResourceWithEnvironment, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("name".to_string(), serde_json::to_value(&name).unwrap_or(Value::Null));
        args.insert("value".to_string(), serde_json::to_value(&value).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting/withEnvironment", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResourceWithEnvironment::new(handle, self.client.clone()))
    }

    /// Sets an environment variable from a reference expression
    pub fn with_environment_expression(&self, name: &str, value: ReferenceExpression) -> Result<IResourceWithEnvironment, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("name".to_string(), serde_json::to_value(&name).unwrap_or(Value::Null));
        args.insert("value".to_string(), serde_json::to_value(&value).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting/withEnvironmentExpression", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResourceWithEnvironment::new(handle, self.client.clone()))
    }

    /// Sets environment variables via callback
    pub fn with_environment_callback(&self, callback: impl Fn(Vec<Value>) -> Value + Send + Sync + 'static) -> Result<IResourceWithEnvironment, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        let callback_id = register_callback(callback);
        args.insert("callback".to_string(), Value::String(callback_id));
        let result = self.client.invoke_capability("Aspire.Hosting/withEnvironmentCallback", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResourceWithEnvironment::new(handle, self.client.clone()))
    }

    /// Sets an environment variable from an endpoint reference
    pub fn with_environment_endpoint(&self, name: &str, endpoint_reference: &EndpointReference) -> Result<IResourceWithEnvironment, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("name".to_string(), serde_json::to_value(&name).unwrap_or(Value::Null));
        args.insert("endpointReference".to_string(), endpoint_reference.handle().to_json());
        let result = self.client.invoke_capability("Aspire.Hosting/withEnvironmentEndpoint", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResourceWithEnvironment::new(handle, self.client.clone()))
    }

    /// Sets an environment variable from a parameter resource
    pub fn with_environment_parameter(&self, name: &str, parameter: &ParameterResource) -> Result<IResourceWithEnvironment, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("name".to_string(), serde_json::to_value(&name).unwrap_or(Value::Null));
        args.insert("parameter".to_string(), parameter.handle().to_json());
        let result = self.client.invoke_capability("Aspire.Hosting/withEnvironmentParameter", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResourceWithEnvironment::new(handle, self.client.clone()))
    }

    /// Sets an environment variable from a connection string resource
    pub fn with_environment_connection_string(&self, env_var_name: &str, resource: &IResourceWithConnectionString) -> Result<IResourceWithEnvironment, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("envVarName".to_string(), serde_json::to_value(&env_var_name).unwrap_or(Value::Null));
        args.insert("resource".to_string(), resource.handle().to_json());
        let result = self.client.invoke_capability("Aspire.Hosting/withEnvironmentConnectionString", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResourceWithEnvironment::new(handle, self.client.clone()))
    }

    /// Adds arguments
    pub fn with_args(&self, args: Vec<String>) -> Result<IResourceWithArgs, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("args".to_string(), serde_json::to_value(&args).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting/withArgs", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResourceWithArgs::new(handle, self.client.clone()))
    }

    /// Sets command-line arguments via callback
    pub fn with_args_callback(&self, callback: impl Fn(Vec<Value>) -> Value + Send + Sync + 'static) -> Result<IResourceWithArgs, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        let callback_id = register_callback(callback);
        args.insert("callback".to_string(), Value::String(callback_id));
        let result = self.client.invoke_capability("Aspire.Hosting/withArgsCallback", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResourceWithArgs::new(handle, self.client.clone()))
    }

    /// Adds a reference to another resource
    pub fn with_reference(&self, source: &IResource, connection_name: Option<&str>, optional: Option<bool>, name: Option<&str>) -> Result<IResourceWithEnvironment, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("source".to_string(), source.handle().to_json());
        if let Some(ref v) = connection_name {
            args.insert("connectionName".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        if let Some(ref v) = optional {
            args.insert("optional".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        if let Some(ref v) = name {
            args.insert("name".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        let result = self.client.invoke_capability("Aspire.Hosting/withReference", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResourceWithEnvironment::new(handle, self.client.clone()))
    }

    /// Adds a reference to a URI
    pub fn with_reference_uri(&self, name: &str, uri: &str) -> Result<IResourceWithEnvironment, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("name".to_string(), serde_json::to_value(&name).unwrap_or(Value::Null));
        args.insert("uri".to_string(), serde_json::to_value(&uri).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting/withReferenceUri", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResourceWithEnvironment::new(handle, self.client.clone()))
    }

    /// Adds a reference to an external service
    pub fn with_reference_external_service(&self, external_service: &ExternalServiceResource) -> Result<IResourceWithEnvironment, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("externalService".to_string(), external_service.handle().to_json());
        let result = self.client.invoke_capability("Aspire.Hosting/withReferenceExternalService", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResourceWithEnvironment::new(handle, self.client.clone()))
    }

    /// Adds a reference to an endpoint
    pub fn with_reference_endpoint(&self, endpoint_reference: &EndpointReference) -> Result<IResourceWithEnvironment, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("endpointReference".to_string(), endpoint_reference.handle().to_json());
        let result = self.client.invoke_capability("Aspire.Hosting/withReferenceEndpoint", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResourceWithEnvironment::new(handle, self.client.clone()))
    }

    /// Adds a network endpoint
    pub fn with_endpoint(&self, port: Option<f64>, target_port: Option<f64>, scheme: Option<&str>, name: Option<&str>, env: Option<&str>, is_proxied: Option<bool>, is_external: Option<bool>, protocol: Option<ProtocolType>) -> Result<IResourceWithEndpoints, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        if let Some(ref v) = port {
            args.insert("port".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        if let Some(ref v) = target_port {
            args.insert("targetPort".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        if let Some(ref v) = scheme {
            args.insert("scheme".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        if let Some(ref v) = name {
            args.insert("name".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        if let Some(ref v) = env {
            args.insert("env".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        if let Some(ref v) = is_proxied {
            args.insert("isProxied".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        if let Some(ref v) = is_external {
            args.insert("isExternal".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        if let Some(ref v) = protocol {
            args.insert("protocol".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        let result = self.client.invoke_capability("Aspire.Hosting/withEndpoint", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResourceWithEndpoints::new(handle, self.client.clone()))
    }

    /// Adds an HTTP endpoint
    pub fn with_http_endpoint(&self, port: Option<f64>, target_port: Option<f64>, name: Option<&str>, env: Option<&str>, is_proxied: Option<bool>) -> Result<IResourceWithEndpoints, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        if let Some(ref v) = port {
            args.insert("port".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        if let Some(ref v) = target_port {
            args.insert("targetPort".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        if let Some(ref v) = name {
            args.insert("name".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        if let Some(ref v) = env {
            args.insert("env".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        if let Some(ref v) = is_proxied {
            args.insert("isProxied".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        let result = self.client.invoke_capability("Aspire.Hosting/withHttpEndpoint", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResourceWithEndpoints::new(handle, self.client.clone()))
    }

    /// Adds an HTTPS endpoint
    pub fn with_https_endpoint(&self, port: Option<f64>, target_port: Option<f64>, name: Option<&str>, env: Option<&str>, is_proxied: Option<bool>) -> Result<IResourceWithEndpoints, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        if let Some(ref v) = port {
            args.insert("port".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        if let Some(ref v) = target_port {
            args.insert("targetPort".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        if let Some(ref v) = name {
            args.insert("name".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        if let Some(ref v) = env {
            args.insert("env".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        if let Some(ref v) = is_proxied {
            args.insert("isProxied".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        let result = self.client.invoke_capability("Aspire.Hosting/withHttpsEndpoint", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResourceWithEndpoints::new(handle, self.client.clone()))
    }

    /// Makes HTTP endpoints externally accessible
    pub fn with_external_http_endpoints(&self) -> Result<IResourceWithEndpoints, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        let result = self.client.invoke_capability("Aspire.Hosting/withExternalHttpEndpoints", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResourceWithEndpoints::new(handle, self.client.clone()))
    }

    /// Gets an endpoint reference
    pub fn get_endpoint(&self, name: &str) -> Result<EndpointReference, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("name".to_string(), serde_json::to_value(&name).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting/getEndpoint", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(EndpointReference::new(handle, self.client.clone()))
    }

    /// Configures resource for HTTP/2
    pub fn as_http2_service(&self) -> Result<IResourceWithEndpoints, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        let result = self.client.invoke_capability("Aspire.Hosting/asHttp2Service", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResourceWithEndpoints::new(handle, self.client.clone()))
    }

    /// Customizes displayed URLs via callback
    pub fn with_urls_callback(&self, callback: impl Fn(Vec<Value>) -> Value + Send + Sync + 'static) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        let callback_id = register_callback(callback);
        args.insert("callback".to_string(), Value::String(callback_id));
        let result = self.client.invoke_capability("Aspire.Hosting/withUrlsCallback", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Adds or modifies displayed URLs
    pub fn with_url(&self, url: &str, display_text: Option<&str>) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("url".to_string(), serde_json::to_value(&url).unwrap_or(Value::Null));
        if let Some(ref v) = display_text {
            args.insert("displayText".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        let result = self.client.invoke_capability("Aspire.Hosting/withUrl", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Adds a URL using a reference expression
    pub fn with_url_expression(&self, url: ReferenceExpression, display_text: Option<&str>) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("url".to_string(), serde_json::to_value(&url).unwrap_or(Value::Null));
        if let Some(ref v) = display_text {
            args.insert("displayText".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        let result = self.client.invoke_capability("Aspire.Hosting/withUrlExpression", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Customizes the URL for a specific endpoint via callback
    pub fn with_url_for_endpoint(&self, endpoint_name: &str, callback: impl Fn(Vec<Value>) -> Value + Send + Sync + 'static) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("endpointName".to_string(), serde_json::to_value(&endpoint_name).unwrap_or(Value::Null));
        let callback_id = register_callback(callback);
        args.insert("callback".to_string(), Value::String(callback_id));
        let result = self.client.invoke_capability("Aspire.Hosting/withUrlForEndpoint", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Adds a URL for a specific endpoint via factory callback
    pub fn with_url_for_endpoint_factory(&self, endpoint_name: &str, callback: impl Fn(Vec<Value>) -> Value + Send + Sync + 'static) -> Result<IResourceWithEndpoints, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("endpointName".to_string(), serde_json::to_value(&endpoint_name).unwrap_or(Value::Null));
        let callback_id = register_callback(callback);
        args.insert("callback".to_string(), Value::String(callback_id));
        let result = self.client.invoke_capability("Aspire.Hosting/withUrlForEndpointFactory", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResourceWithEndpoints::new(handle, self.client.clone()))
    }

    /// Excludes the resource from the deployment manifest
    pub fn exclude_from_manifest(&self) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        let result = self.client.invoke_capability("Aspire.Hosting/excludeFromManifest", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Waits for another resource to be ready
    pub fn wait_for(&self, dependency: &IResource) -> Result<IResourceWithWaitSupport, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("dependency".to_string(), dependency.handle().to_json());
        let result = self.client.invoke_capability("Aspire.Hosting/waitForResource", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResourceWithWaitSupport::new(handle, self.client.clone()))
    }

    /// Waits for another resource with specific behavior
    pub fn wait_for_with_behavior(&self, dependency: &IResource, wait_behavior: WaitBehavior) -> Result<IResourceWithWaitSupport, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("dependency".to_string(), dependency.handle().to_json());
        args.insert("waitBehavior".to_string(), serde_json::to_value(&wait_behavior).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting/waitForWithBehavior", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResourceWithWaitSupport::new(handle, self.client.clone()))
    }

    /// Waits for another resource to start
    pub fn wait_for_start(&self, dependency: &IResource) -> Result<IResourceWithWaitSupport, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("dependency".to_string(), dependency.handle().to_json());
        let result = self.client.invoke_capability("Aspire.Hosting/waitForResourceStart", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResourceWithWaitSupport::new(handle, self.client.clone()))
    }

    /// Waits for another resource to start with specific behavior
    pub fn wait_for_start_with_behavior(&self, dependency: &IResource, wait_behavior: WaitBehavior) -> Result<IResourceWithWaitSupport, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("dependency".to_string(), dependency.handle().to_json());
        args.insert("waitBehavior".to_string(), serde_json::to_value(&wait_behavior).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting/waitForStartWithBehavior", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResourceWithWaitSupport::new(handle, self.client.clone()))
    }

    /// Prevents resource from starting automatically
    pub fn with_explicit_start(&self) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        let result = self.client.invoke_capability("Aspire.Hosting/withExplicitStart", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Waits for resource completion
    pub fn wait_for_completion(&self, dependency: &IResource, exit_code: Option<f64>) -> Result<IResourceWithWaitSupport, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("dependency".to_string(), dependency.handle().to_json());
        if let Some(ref v) = exit_code {
            args.insert("exitCode".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        let result = self.client.invoke_capability("Aspire.Hosting/waitForResourceCompletion", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResourceWithWaitSupport::new(handle, self.client.clone()))
    }

    /// Adds a health check by key
    pub fn with_health_check(&self, key: &str) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("key".to_string(), serde_json::to_value(&key).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting/withHealthCheck", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Adds an HTTP health check
    pub fn with_http_health_check(&self, path: Option<&str>, status_code: Option<f64>, endpoint_name: Option<&str>) -> Result<IResourceWithEndpoints, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        if let Some(ref v) = path {
            args.insert("path".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        if let Some(ref v) = status_code {
            args.insert("statusCode".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        if let Some(ref v) = endpoint_name {
            args.insert("endpointName".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        let result = self.client.invoke_capability("Aspire.Hosting/withHttpHealthCheck", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResourceWithEndpoints::new(handle, self.client.clone()))
    }

    /// Adds a resource command
    pub fn with_command(&self, name: &str, display_name: &str, execute_command: impl Fn(Vec<Value>) -> Value + Send + Sync + 'static, command_options: Option<CommandOptions>) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("name".to_string(), serde_json::to_value(&name).unwrap_or(Value::Null));
        args.insert("displayName".to_string(), serde_json::to_value(&display_name).unwrap_or(Value::Null));
        let callback_id = register_callback(execute_command);
        args.insert("executeCommand".to_string(), Value::String(callback_id));
        if let Some(ref v) = command_options {
            args.insert("commandOptions".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        let result = self.client.invoke_capability("Aspire.Hosting/withCommand", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Configures developer certificate trust
    pub fn with_developer_certificate_trust(&self, trust: bool) -> Result<IResourceWithEnvironment, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("trust".to_string(), serde_json::to_value(&trust).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting/withDeveloperCertificateTrust", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResourceWithEnvironment::new(handle, self.client.clone()))
    }

    /// Sets the certificate trust scope
    pub fn with_certificate_trust_scope(&self, scope: CertificateTrustScope) -> Result<IResourceWithEnvironment, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("scope".to_string(), serde_json::to_value(&scope).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting/withCertificateTrustScope", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResourceWithEnvironment::new(handle, self.client.clone()))
    }

    /// Configures HTTPS with a developer certificate
    pub fn with_https_developer_certificate(&self, password: Option<&ParameterResource>) -> Result<IResourceWithEnvironment, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        if let Some(ref v) = password {
            args.insert("password".to_string(), v.handle().to_json());
        }
        let result = self.client.invoke_capability("Aspire.Hosting/withParameterHttpsDeveloperCertificate", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResourceWithEnvironment::new(handle, self.client.clone()))
    }

    /// Removes HTTPS certificate configuration
    pub fn without_https_certificate(&self) -> Result<IResourceWithEnvironment, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        let result = self.client.invoke_capability("Aspire.Hosting/withoutHttpsCertificate", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResourceWithEnvironment::new(handle, self.client.clone()))
    }

    /// Sets the parent relationship
    pub fn with_parent_relationship(&self, parent: &IResource) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("parent".to_string(), parent.handle().to_json());
        let result = self.client.invoke_capability("Aspire.Hosting/withBuilderParentRelationship", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Sets a child relationship
    pub fn with_child_relationship(&self, child: &IResource) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("child".to_string(), child.handle().to_json());
        let result = self.client.invoke_capability("Aspire.Hosting/withBuilderChildRelationship", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Sets the icon for the resource
    pub fn with_icon_name(&self, icon_name: &str, icon_variant: Option<IconVariant>) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("iconName".to_string(), serde_json::to_value(&icon_name).unwrap_or(Value::Null));
        if let Some(ref v) = icon_variant {
            args.insert("iconVariant".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        let result = self.client.invoke_capability("Aspire.Hosting/withIconName", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Adds an HTTP health probe to the resource
    pub fn with_http_probe(&self, probe_type: ProbeType, path: Option<&str>, initial_delay_seconds: Option<f64>, period_seconds: Option<f64>, timeout_seconds: Option<f64>, failure_threshold: Option<f64>, success_threshold: Option<f64>, endpoint_name: Option<&str>) -> Result<IResourceWithEndpoints, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("probeType".to_string(), serde_json::to_value(&probe_type).unwrap_or(Value::Null));
        if let Some(ref v) = path {
            args.insert("path".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        if let Some(ref v) = initial_delay_seconds {
            args.insert("initialDelaySeconds".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        if let Some(ref v) = period_seconds {
            args.insert("periodSeconds".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        if let Some(ref v) = timeout_seconds {
            args.insert("timeoutSeconds".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        if let Some(ref v) = failure_threshold {
            args.insert("failureThreshold".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        if let Some(ref v) = success_threshold {
            args.insert("successThreshold".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        if let Some(ref v) = endpoint_name {
            args.insert("endpointName".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        let result = self.client.invoke_capability("Aspire.Hosting/withHttpProbe", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResourceWithEndpoints::new(handle, self.client.clone()))
    }

    /// Excludes the resource from MCP server exposure
    pub fn exclude_from_mcp(&self) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        let result = self.client.invoke_capability("Aspire.Hosting/excludeFromMcp", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Sets the remote image name for publishing
    pub fn with_remote_image_name(&self, remote_image_name: &str) -> Result<IComputeResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("remoteImageName".to_string(), serde_json::to_value(&remote_image_name).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting/withRemoteImageName", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IComputeResource::new(handle, self.client.clone()))
    }

    /// Sets the remote image tag for publishing
    pub fn with_remote_image_tag(&self, remote_image_tag: &str) -> Result<IComputeResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("remoteImageTag".to_string(), serde_json::to_value(&remote_image_tag).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting/withRemoteImageTag", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IComputeResource::new(handle, self.client.clone()))
    }

    /// Adds a pipeline step to the resource
    pub fn with_pipeline_step_factory(&self, step_name: &str, callback: impl Fn(Vec<Value>) -> Value + Send + Sync + 'static, depends_on: Option<Vec<String>>, required_by: Option<Vec<String>>, tags: Option<Vec<String>>, description: Option<&str>) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("stepName".to_string(), serde_json::to_value(&step_name).unwrap_or(Value::Null));
        let callback_id = register_callback(callback);
        args.insert("callback".to_string(), Value::String(callback_id));
        if let Some(ref v) = depends_on {
            args.insert("dependsOn".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        if let Some(ref v) = required_by {
            args.insert("requiredBy".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        if let Some(ref v) = tags {
            args.insert("tags".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        if let Some(ref v) = description {
            args.insert("description".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        let result = self.client.invoke_capability("Aspire.Hosting/withPipelineStepFactory", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Configures pipeline step dependencies via a callback
    pub fn with_pipeline_configuration(&self, callback: impl Fn(Vec<Value>) -> Value + Send + Sync + 'static) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        let callback_id = register_callback(callback);
        args.insert("callback".to_string(), Value::String(callback_id));
        let result = self.client.invoke_capability("Aspire.Hosting/withPipelineConfiguration", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Adds a volume
    pub fn with_volume(&self, target: &str, name: Option<&str>, is_read_only: Option<bool>) -> Result<ContainerResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("resource".to_string(), self.handle.to_json());
        args.insert("target".to_string(), serde_json::to_value(&target).unwrap_or(Value::Null));
        if let Some(ref v) = name {
            args.insert("name".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        if let Some(ref v) = is_read_only {
            args.insert("isReadOnly".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        let result = self.client.invoke_capability("Aspire.Hosting/withVolume", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(ContainerResource::new(handle, self.client.clone()))
    }

    /// Gets the resource name
    pub fn get_resource_name(&self) -> Result<String, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("resource".to_string(), self.handle.to_json());
        let result = self.client.invoke_capability("Aspire.Hosting/getResourceName", args)?;
        Ok(serde_json::from_value(result)?)
    }

    /// Subscribes to the BeforeResourceStarted event
    pub fn on_before_resource_started(&self, callback: impl Fn(Vec<Value>) -> Value + Send + Sync + 'static) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        let callback_id = register_callback(callback);
        args.insert("callback".to_string(), Value::String(callback_id));
        let result = self.client.invoke_capability("Aspire.Hosting/onBeforeResourceStarted", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Subscribes to the ResourceStopped event
    pub fn on_resource_stopped(&self, callback: impl Fn(Vec<Value>) -> Value + Send + Sync + 'static) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        let callback_id = register_callback(callback);
        args.insert("callback".to_string(), Value::String(callback_id));
        let result = self.client.invoke_capability("Aspire.Hosting/onResourceStopped", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Subscribes to the InitializeResource event
    pub fn on_initialize_resource(&self, callback: impl Fn(Vec<Value>) -> Value + Send + Sync + 'static) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        let callback_id = register_callback(callback);
        args.insert("callback".to_string(), Value::String(callback_id));
        let result = self.client.invoke_capability("Aspire.Hosting/onInitializeResource", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Subscribes to the ResourceEndpointsAllocated event
    pub fn on_resource_endpoints_allocated(&self, callback: impl Fn(Vec<Value>) -> Value + Send + Sync + 'static) -> Result<IResourceWithEndpoints, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        let callback_id = register_callback(callback);
        args.insert("callback".to_string(), Value::String(callback_id));
        let result = self.client.invoke_capability("Aspire.Hosting/onResourceEndpointsAllocated", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResourceWithEndpoints::new(handle, self.client.clone()))
    }

    /// Subscribes to the ResourceReady event
    pub fn on_resource_ready(&self, callback: impl Fn(Vec<Value>) -> Value + Send + Sync + 'static) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        let callback_id = register_callback(callback);
        args.insert("callback".to_string(), Value::String(callback_id));
        let result = self.client.invoke_capability("Aspire.Hosting/onResourceReady", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
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

    /// Adds a dependency from a string or another resource
    pub fn with_union_dependency(&self, dependency: Value) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("dependency".to_string(), serde_json::to_value(&dependency).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting.CodeGeneration.Rust.Tests/withUnionDependency", args)?;
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

    /// Adds a label to the resource
    pub fn with_merge_label(&self, label: &str) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("label".to_string(), serde_json::to_value(&label).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting.CodeGeneration.Rust.Tests/withMergeLabel", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Adds a categorized label to the resource
    pub fn with_merge_label_categorized(&self, label: &str, category: &str) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("label".to_string(), serde_json::to_value(&label).unwrap_or(Value::Null));
        args.insert("category".to_string(), serde_json::to_value(&category).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting.CodeGeneration.Rust.Tests/withMergeLabelCategorized", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Configures a named endpoint
    pub fn with_merge_endpoint(&self, endpoint_name: &str, port: f64) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("endpointName".to_string(), serde_json::to_value(&endpoint_name).unwrap_or(Value::Null));
        args.insert("port".to_string(), serde_json::to_value(&port).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting.CodeGeneration.Rust.Tests/withMergeEndpoint", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Configures a named endpoint with scheme
    pub fn with_merge_endpoint_scheme(&self, endpoint_name: &str, port: f64, scheme: &str) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("endpointName".to_string(), serde_json::to_value(&endpoint_name).unwrap_or(Value::Null));
        args.insert("port".to_string(), serde_json::to_value(&port).unwrap_or(Value::Null));
        args.insert("scheme".to_string(), serde_json::to_value(&scheme).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting.CodeGeneration.Rust.Tests/withMergeEndpointScheme", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Configures resource logging
    pub fn with_merge_logging(&self, log_level: &str, enable_console: Option<bool>, max_files: Option<f64>) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("logLevel".to_string(), serde_json::to_value(&log_level).unwrap_or(Value::Null));
        if let Some(ref v) = enable_console {
            args.insert("enableConsole".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        if let Some(ref v) = max_files {
            args.insert("maxFiles".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        let result = self.client.invoke_capability("Aspire.Hosting.CodeGeneration.Rust.Tests/withMergeLogging", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Configures resource logging with file path
    pub fn with_merge_logging_path(&self, log_level: &str, log_path: &str, enable_console: Option<bool>, max_files: Option<f64>) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("logLevel".to_string(), serde_json::to_value(&log_level).unwrap_or(Value::Null));
        args.insert("logPath".to_string(), serde_json::to_value(&log_path).unwrap_or(Value::Null));
        if let Some(ref v) = enable_console {
            args.insert("enableConsole".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        if let Some(ref v) = max_files {
            args.insert("maxFiles".to_string(), serde_json::to_value(v).unwrap_or(Value::Null));
        }
        let result = self.client.invoke_capability("Aspire.Hosting.CodeGeneration.Rust.Tests/withMergeLoggingPath", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Configures a route
    pub fn with_merge_route(&self, path: &str, method: &str, handler: &str, priority: f64) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("path".to_string(), serde_json::to_value(&path).unwrap_or(Value::Null));
        args.insert("method".to_string(), serde_json::to_value(&method).unwrap_or(Value::Null));
        args.insert("handler".to_string(), serde_json::to_value(&handler).unwrap_or(Value::Null));
        args.insert("priority".to_string(), serde_json::to_value(&priority).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting.CodeGeneration.Rust.Tests/withMergeRoute", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }

    /// Configures a route with middleware
    pub fn with_merge_route_middleware(&self, path: &str, method: &str, handler: &str, priority: f64, middleware: &str) -> Result<IResource, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("builder".to_string(), self.handle.to_json());
        args.insert("path".to_string(), serde_json::to_value(&path).unwrap_or(Value::Null));
        args.insert("method".to_string(), serde_json::to_value(&method).unwrap_or(Value::Null));
        args.insert("handler".to_string(), serde_json::to_value(&handler).unwrap_or(Value::Null));
        args.insert("priority".to_string(), serde_json::to_value(&priority).unwrap_or(Value::Null));
        args.insert("middleware".to_string(), serde_json::to_value(&middleware).unwrap_or(Value::Null));
        let result = self.client.invoke_capability("Aspire.Hosting.CodeGeneration.Rust.Tests/withMergeRouteMiddleware", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IResource::new(handle, self.client.clone()))
    }
}

/// Wrapper for Aspire.Hosting/Aspire.Hosting.ApplicationModel.UpdateCommandStateContext
pub struct UpdateCommandStateContext {
    handle: Handle,
    client: Arc<AspireClient>,
}

impl HasHandle for UpdateCommandStateContext {
    fn handle(&self) -> &Handle {
        &self.handle
    }
}

impl UpdateCommandStateContext {
    pub fn new(handle: Handle, client: Arc<AspireClient>) -> Self {
        Self { handle, client }
    }

    pub fn handle(&self) -> &Handle {
        &self.handle
    }

    pub fn client(&self) -> &Arc<AspireClient> {
        &self.client
    }

    /// Gets the ServiceProvider property
    pub fn service_provider(&self) -> Result<IServiceProvider, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("context".to_string(), self.handle.to_json());
        let result = self.client.invoke_capability("Aspire.Hosting.ApplicationModel/UpdateCommandStateContext.serviceProvider", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(IServiceProvider::new(handle, self.client.clone()))
    }

    /// Sets the ServiceProvider property
    pub fn set_service_provider(&self, value: &IServiceProvider) -> Result<UpdateCommandStateContext, Box<dyn std::error::Error>> {
        let mut args: HashMap<String, Value> = HashMap::new();
        args.insert("context".to_string(), self.handle.to_json());
        args.insert("value".to_string(), value.handle().to_json());
        let result = self.client.invoke_capability("Aspire.Hosting.ApplicationModel/UpdateCommandStateContext.setServiceProvider", args)?;
        let handle: Handle = serde_json::from_value(result)?;
        Ok(UpdateCommandStateContext::new(handle, self.client.clone()))
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

