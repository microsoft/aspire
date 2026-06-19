//! Serializable data-transfer objects sent to the UI, plus conversions from the
//! generated protobuf types. Keeping the wire format to the UI as plain
//! `camelCase` JSON (rather than raw protobuf) keeps the frontend simple and
//! decoupled from the gRPC contract. See `../CONTRACT.md` for the authoritative
//! shapes.

use serde::Serialize;

use crate::proto::aspire::v1 as pb;

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct ResourceUrl {
    pub name: Option<String>,
    pub url: String,
    pub is_internal: bool,
    pub is_inactive: bool,
    pub display_name: Option<String>,
    pub sort_order: i32,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct ResourceProperty {
    pub name: String,
    pub display_name: Option<String>,
    pub value: String,
    pub is_sensitive: bool,
    pub is_highlighted: bool,
    pub sort_order: Option<i32>,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct EnvVar {
    pub name: String,
    pub value: Option<String>,
    pub is_from_spec: bool,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct HealthReport {
    pub status: Option<String>,
    pub key: String,
    pub description: String,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct ResourceCommand {
    pub name: String,
    pub display_name: String,
    pub display_description: Option<String>,
    pub confirmation_message: Option<String>,
    pub icon_name: Option<String>,
    pub is_highlighted: bool,
    pub state: String,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct ResourceRelationship {
    pub resource_name: String,
    #[serde(rename = "type")]
    pub kind: String,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct Resource {
    pub name: String,
    pub resource_type: String,
    pub display_name: String,
    pub uid: String,
    pub state: Option<String>,
    pub state_style: Option<String>,
    pub health: Option<String>,
    pub created_at: Option<String>,
    pub started_at: Option<String>,
    pub stopped_at: Option<String>,
    pub urls: Vec<ResourceUrl>,
    pub properties: Vec<ResourceProperty>,
    pub environment: Vec<EnvVar>,
    pub health_reports: Vec<HealthReport>,
    pub commands: Vec<ResourceCommand>,
    pub relationships: Vec<ResourceRelationship>,
    pub is_hidden: bool,
    pub supports_detailed_telemetry: bool,
    pub icon_name: Option<String>,
}

fn timestamp_to_rfc3339(ts: &prost_types::Timestamp) -> Option<String> {
    chrono::DateTime::from_timestamp(ts.seconds, ts.nanos.max(0) as u32)
        .map(|dt| dt.to_rfc3339())
}

/// Converts a `google.protobuf.Value` to a `serde_json::Value` so structured
/// property values (structs, lists) round-trip faithfully.
fn prost_value_to_json(value: &prost_types::Value) -> serde_json::Value {
    use prost_types::value::Kind;
    match &value.kind {
        Some(Kind::NullValue(_)) | None => serde_json::Value::Null,
        Some(Kind::NumberValue(n)) => serde_json::Number::from_f64(*n)
            .map(serde_json::Value::Number)
            .unwrap_or(serde_json::Value::Null),
        Some(Kind::StringValue(s)) => serde_json::Value::String(s.clone()),
        Some(Kind::BoolValue(b)) => serde_json::Value::Bool(*b),
        Some(Kind::StructValue(s)) => {
            let map = s
                .fields
                .iter()
                .map(|(k, v)| (k.clone(), prost_value_to_json(v)))
                .collect();
            serde_json::Value::Object(map)
        }
        Some(Kind::ListValue(l)) => {
            serde_json::Value::Array(l.values.iter().map(prost_value_to_json).collect())
        }
    }
}

/// Renders a property value to a display string. Plain scalars render directly;
/// structured values render as compact JSON.
fn render_property_value(value: Option<&prost_types::Value>) -> String {
    match value {
        None => String::new(),
        Some(v) => match prost_value_to_json(v) {
            serde_json::Value::Null => String::new(),
            serde_json::Value::String(s) => s,
            serde_json::Value::Bool(b) => b.to_string(),
            serde_json::Value::Number(n) => n.to_string(),
            other => other.to_string(),
        },
    }
}

fn health_status_label(status: i32) -> &'static str {
    match pb::HealthStatus::try_from(status) {
        Ok(pb::HealthStatus::Healthy) => "Healthy",
        Ok(pb::HealthStatus::Unhealthy) => "Unhealthy",
        Ok(pb::HealthStatus::Degraded) => "Degraded",
        Err(_) => "Unknown",
    }
}

fn command_state_label(state: i32) -> &'static str {
    match pb::ResourceCommandState::try_from(state) {
        Ok(pb::ResourceCommandState::Enabled) => "enabled",
        Ok(pb::ResourceCommandState::Disabled) => "disabled",
        Ok(pb::ResourceCommandState::Hidden) => "hidden",
        Err(_) => "enabled",
    }
}

/// Aggregates per-check health reports into a single resource-level status using
/// the worst observed status: any Unhealthy wins, then Degraded, otherwise
/// Healthy when at least one report exists.
fn aggregate_health(reports: &[pb::HealthReport]) -> Option<String> {
    if reports.is_empty() {
        return None;
    }
    let mut any = false;
    let mut degraded = false;
    for report in reports {
        match report.status {
            Some(status) => {
                any = true;
                match pb::HealthStatus::try_from(status) {
                    Ok(pb::HealthStatus::Unhealthy) => return Some("Unhealthy".to_string()),
                    Ok(pb::HealthStatus::Degraded) => degraded = true,
                    _ => {}
                }
            }
            None => {}
        }
    }
    if !any {
        None
    } else if degraded {
        Some("Degraded".to_string())
    } else {
        Some("Healthy".to_string())
    }
}

impl From<&pb::Resource> for Resource {
    fn from(r: &pb::Resource) -> Self {
        let urls = r
            .urls
            .iter()
            .map(|u| ResourceUrl {
                name: u.endpoint_name.clone(),
                url: u.full_url.clone(),
                is_internal: u.is_internal,
                is_inactive: u.is_inactive,
                display_name: u
                    .display_properties
                    .as_ref()
                    .map(|d| d.display_name.clone())
                    .filter(|s| !s.is_empty()),
                sort_order: u
                    .display_properties
                    .as_ref()
                    .map(|d| d.sort_order)
                    .unwrap_or(0),
            })
            .collect();

        let properties = r
            .properties
            .iter()
            .map(|p| ResourceProperty {
                name: p.name.clone(),
                display_name: p.display_name.clone(),
                value: render_property_value(p.value.as_ref()),
                is_sensitive: p.is_sensitive.unwrap_or(false),
                is_highlighted: p.is_highlighted,
                sort_order: p.sort_order,
            })
            .collect();

        let environment = r
            .environment
            .iter()
            .map(|e| EnvVar {
                name: e.name.clone(),
                value: e.value.clone(),
                is_from_spec: e.is_from_spec,
            })
            .collect();

        let health_reports = r
            .health_reports
            .iter()
            .map(|h| HealthReport {
                status: h.status.map(|s| health_status_label(s).to_string()),
                key: h.key.clone(),
                description: h.description.clone(),
            })
            .collect();

        let commands = r
            .commands
            .iter()
            .map(|c| ResourceCommand {
                name: c.name.clone(),
                display_name: c.display_name.clone(),
                display_description: c.display_description.clone(),
                confirmation_message: c.confirmation_message.clone(),
                icon_name: c.icon_name.clone(),
                is_highlighted: c.is_highlighted,
                state: command_state_label(c.state).to_string(),
            })
            .collect();

        let relationships = r
            .relationships
            .iter()
            .map(|rel| ResourceRelationship {
                resource_name: rel.resource_name.clone(),
                kind: rel.r#type.clone(),
            })
            .collect();

        Resource {
            name: r.name.clone(),
            resource_type: r.resource_type.clone(),
            display_name: if r.display_name.is_empty() {
                r.name.clone()
            } else {
                r.display_name.clone()
            },
            uid: r.uid.clone(),
            state: r.state.clone(),
            state_style: r.state_style.clone(),
            health: aggregate_health(&r.health_reports),
            created_at: r.created_at.as_ref().and_then(timestamp_to_rfc3339),
            started_at: r.started_at.as_ref().and_then(timestamp_to_rfc3339),
            stopped_at: r.stopped_at.as_ref().and_then(timestamp_to_rfc3339),
            urls,
            properties,
            environment,
            health_reports,
            commands,
            relationships,
            is_hidden: r.is_hidden,
            supports_detailed_telemetry: r.supports_detailed_telemetry,
            icon_name: r.icon_name.clone(),
        }
    }
}

/// Event payload for `deck://resources`.
#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct ResourcesEvent {
    #[serde(rename = "type")]
    pub kind: ResourcesEventKind,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub resources: Option<Vec<Resource>>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub upserts: Option<Vec<Resource>>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub deletes: Option<Vec<String>>,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub enum ResourcesEventKind {
    Snapshot,
    Change,
}

impl ResourcesEvent {
    pub fn snapshot(resources: Vec<Resource>) -> Self {
        ResourcesEvent {
            kind: ResourcesEventKind::Snapshot,
            resources: Some(resources),
            upserts: None,
            deletes: None,
        }
    }

    pub fn change(upserts: Vec<Resource>, deletes: Vec<String>) -> Self {
        ResourcesEvent {
            kind: ResourcesEventKind::Change,
            resources: None,
            upserts: Some(upserts),
            deletes: Some(deletes),
        }
    }
}

/// Console log payload for `deck://console-log`.
#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct ConsoleLogLine {
    pub line_number: i32,
    pub text: String,
    pub is_std_err: bool,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct ConsoleLogEvent {
    pub resource_name: String,
    pub lines: Vec<ConsoleLogLine>,
}

/// Connection status payload for `deck://connection`.
#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct ConnectionStatus {
    pub target: String,
    pub state: String,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub message: Option<String>,
}

impl ConnectionStatus {
    pub fn new(target: &str, state: &str, message: Option<String>) -> Self {
        ConnectionStatus {
            target: target.to_string(),
            state: state.to_string(),
            message,
        }
    }
}

/// Response from executing a resource command.
#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct CommandResponse {
    pub kind: String,
    pub message: Option<String>,
}
