//! Client for the AppHost resource service (`aspire.v1.DashboardService`). This
//! speaks exactly the same gRPC contract the Blazor dashboard's
//! `DashboardClient` uses, so the AppHost cannot tell the difference between the
//! two dashboards.

use std::time::Duration;

use tauri::{AppHandle, Emitter};
use tonic::metadata::{Ascii, MetadataValue};
use tonic::service::interceptor::InterceptedService;
use tonic::service::Interceptor;
use tonic::transport::{Channel, ClientTlsConfig};
use tonic::{Request, Status};

use crate::config::AuthMode;
use crate::model::{
    CommandResponse, ConnectionStatus, ConsoleLogEvent, ConsoleLogLine, Resource, ResourcesEvent,
};
use crate::proto::aspire::v1::dashboard_service_client::DashboardServiceClient;
use crate::proto::aspire::v1::{
    watch_resources_change, watch_resources_update, ApplicationInformationRequest,
    ResourceCommandRequest, ResourceCommandResponseKind, WatchResourceConsoleLogsRequest,
    WatchResourcesRequest,
};
use crate::state::AppState;

const RECONNECT_DELAY: Duration = Duration::from_secs(2);

/// Adds the `x-resource-service-api-key` header to outgoing requests when API-key
/// authentication is configured.
#[derive(Clone)]
pub struct ApiKeyInterceptor {
    key: Option<MetadataValue<Ascii>>,
}

impl Interceptor for ApiKeyInterceptor {
    fn call(&mut self, mut request: Request<()>) -> Result<Request<()>, Status> {
        if let Some(key) = &self.key {
            request
                .metadata_mut()
                .insert("x-resource-service-api-key", key.clone());
        }
        Ok(request)
    }
}

pub type ResourceServiceClient =
    DashboardServiceClient<InterceptedService<Channel, ApiKeyInterceptor>>;

fn build_interceptor(state: &AppState) -> ApiKeyInterceptor {
    let key = if state.config.resource_service.auth_mode == AuthMode::ApiKey {
        state
            .config
            .resource_service
            .api_key
            .as_ref()
            .and_then(|k| MetadataValue::try_from(k.as_str()).ok())
    } else {
        None
    };
    ApiKeyInterceptor { key }
}

/// Establishes a channel to the resource service endpoint. Uses TLS when the URL
/// scheme is https.
async fn connect_channel(url: &str) -> Result<Channel, String> {
    let mut endpoint = Channel::from_shared(url.to_string())
        .map_err(|e| format!("invalid resource service URL: {e}"))?
        .connect_timeout(Duration::from_secs(10))
        .timeout(Duration::from_secs(30));

    if url.trim_start().starts_with("https") {
        endpoint = endpoint
            .tls_config(ClientTlsConfig::new().with_native_roots())
            .map_err(|e| format!("failed to configure TLS: {e}"))?;
    }

    endpoint.connect().await.map_err(|e| e.to_string())
}

/// Builds a client from the currently-connected channel, if any.
pub fn client_from_state(state: &AppState) -> Option<ResourceServiceClient> {
    let channel = state.channel.lock().unwrap().clone()?;
    Some(DashboardServiceClient::with_interceptor(
        channel,
        build_interceptor(state),
    ))
}

fn emit_connection(app: &AppHandle, state: &str, message: Option<String>) {
    let _ = app.emit(
        "deck://connection",
        &ConnectionStatus::new("resourceService", state, message),
    );
}

/// The long-running task that maintains the resource service connection, watches
/// for resource changes, and pushes them to the UI. Reconnects with a fixed
/// backoff when the stream drops.
pub async fn run_resource_loop(app: AppHandle, state: std::sync::Arc<AppState>) {
    let url = match state.config.resource_service.url.clone() {
        Some(url) => url,
        None => {
            // Standalone mode (e.g. `aspire deck run` with no AppHost). There is
            // no resource service to talk to; surface that clearly and stop.
            emit_connection(
                &app,
                "disconnected",
                Some("No resource service configured".to_string()),
            );
            return;
        }
    };

    let mut is_reconnect = false;

    loop {
        emit_connection(&app, "connecting", Some(url.clone()));

        let channel = match connect_channel(&url).await {
            Ok(channel) => channel,
            Err(err) => {
                emit_connection(&app, "error", Some(err));
                tokio::time::sleep(RECONNECT_DELAY).await;
                is_reconnect = true;
                continue;
            }
        };

        *state.channel.lock().unwrap() = Some(channel.clone());

        let mut client =
            DashboardServiceClient::with_interceptor(channel, build_interceptor(&state));

        // Fetch the application name (best effort).
        if let Ok(info) = client
            .get_application_information(ApplicationInformationRequest {})
            .await
        {
            let name = info.into_inner().application_name;
            *state.application_name.lock().unwrap() = Some(name);
        }

        emit_connection(&app, "connected", Some(url.clone()));

        if let Err(err) = watch_resources(&app, &state, &mut client, is_reconnect).await {
            emit_connection(&app, "disconnected", Some(err));
        } else {
            emit_connection(&app, "disconnected", None);
        }

        // Drop the cached channel so command handlers don't use a dead one.
        *state.channel.lock().unwrap() = None;
        tokio::time::sleep(RECONNECT_DELAY).await;
        is_reconnect = true;
    }
}

async fn watch_resources(
    app: &AppHandle,
    state: &AppState,
    client: &mut ResourceServiceClient,
    is_reconnect: bool,
) -> Result<(), String> {
    let request = WatchResourcesRequest {
        is_reconnect: Some(is_reconnect),
    };

    let mut stream = client
        .watch_resources(request)
        .await
        .map_err(|e| e.to_string())?
        .into_inner();

    while let Some(update) = stream.message().await.map_err(|e| e.to_string())? {
        match update.kind {
            Some(watch_resources_update::Kind::InitialData(initial)) => {
                let resources: Vec<Resource> = initial.resources.iter().map(Resource::from).collect();
                {
                    let mut map = state.resources.lock().unwrap();
                    map.clear();
                    for resource in &resources {
                        map.insert(resource.name.clone(), resource.clone());
                    }
                }
                let _ = app.emit("deck://resources", &ResourcesEvent::snapshot(resources));
            }
            Some(watch_resources_update::Kind::Changes(changes)) => {
                let mut upserts = Vec::new();
                let mut deletes = Vec::new();
                {
                    let mut map = state.resources.lock().unwrap();
                    for change in &changes.value {
                        match &change.kind {
                            Some(watch_resources_change::Kind::Upsert(resource)) => {
                                let dto = Resource::from(resource);
                                map.insert(dto.name.clone(), dto.clone());
                                upserts.push(dto);
                            }
                            Some(watch_resources_change::Kind::Delete(deletion)) => {
                                map.shift_remove(&deletion.resource_name);
                                deletes.push(deletion.resource_name.clone());
                            }
                            None => {}
                        }
                    }
                }
                if !upserts.is_empty() || !deletes.is_empty() {
                    let _ = app.emit("deck://resources", &ResourcesEvent::change(upserts, deletes));
                }
            }
            None => {}
        }
    }

    Ok(())
}

/// Streams console logs for a resource until the stream completes or the task is
/// aborted (via unsubscribe). Each batch of lines is emitted on `deck://console-log`.
pub async fn stream_console_logs(
    app: AppHandle,
    state: std::sync::Arc<AppState>,
    resource_name: String,
) {
    let mut client = match client_from_state(&state) {
        Some(client) => client,
        None => return,
    };

    let request = WatchResourceConsoleLogsRequest {
        resource_name: resource_name.clone(),
        suppress_follow: false,
    };

    let mut stream = match client.watch_resource_console_logs(request).await {
        Ok(response) => response.into_inner(),
        Err(_) => return,
    };

    loop {
        match stream.message().await {
            Ok(Some(update)) => {
                let lines: Vec<ConsoleLogLine> = update
                    .log_lines
                    .into_iter()
                    .map(|line| ConsoleLogLine {
                        line_number: line.line_number,
                        text: line.text,
                        is_std_err: line.is_std_err.unwrap_or(false),
                    })
                    .collect();
                if !lines.is_empty() {
                    let _ = app.emit(
                        "deck://console-log",
                        &ConsoleLogEvent {
                            resource_name: resource_name.clone(),
                            lines,
                        },
                    );
                }
            }
            Ok(None) | Err(_) => break,
        }
    }
}

/// Executes a command against a resource.
// `parameter` and `error_message` are deprecated in the proto but still part of
// the generated struct/response; we set/read them for wire compatibility.
#[allow(deprecated)]
pub async fn execute_command(
    state: std::sync::Arc<AppState>,
    resource_name: String,
    resource_type: String,
    command_name: String,
) -> CommandResponse {
    let mut client = match client_from_state(&state) {
        Some(client) => client,
        None => {
            return CommandResponse {
                kind: "failed".to_string(),
                message: Some("Not connected to the resource service".to_string()),
            }
        }
    };

    let request = ResourceCommandRequest {
        command_name,
        resource_name,
        resource_type,
        parameter: None,
        arguments: Default::default(),
        non_interactive: true,
    };

    match client.execute_resource_command(request).await {
        Ok(response) => {
            let response = response.into_inner();
            let kind = match ResourceCommandResponseKind::try_from(response.kind) {
                Ok(ResourceCommandResponseKind::Succeeded) => "succeeded",
                Ok(ResourceCommandResponseKind::Failed) => "failed",
                Ok(ResourceCommandResponseKind::Cancelled) => "cancelled",
                Ok(ResourceCommandResponseKind::InvalidArguments) => "invalidArguments",
                _ => "undefined",
            };
            CommandResponse {
                kind: kind.to_string(),
                message: response.message.or(response.error_message),
            }
        }
        Err(status) => CommandResponse {
            kind: "failed".to_string(),
            message: Some(status.message().to_string()),
        },
    }
}
