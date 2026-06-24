//! Client for the AppHost resource service (`aspire.v1.DashboardService`). This
//! speaks exactly the same gRPC contract the Blazor dashboard's `DashboardClient`
//! uses, so the AppHost cannot tell the difference between the two dashboards.
//!
//! Aspire Deck can attach to multiple AppHosts; each is a [`Session`] with its own
//! connection. The resource loop runs per session and only pushes events to the UI
//! while its session is the active one (the UI shows one AppHost at a time).

use std::sync::Arc;
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
use crate::state::{AppState, Session};

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

pub(crate) fn build_interceptor(session: &Session) -> ApiKeyInterceptor {
    let key = if session.auth_mode == AuthMode::ApiKey {
        session
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
pub(crate) async fn connect_channel(url: &str) -> Result<Channel, String> {
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

/// Builds a client from a session's currently-connected channel, if any.
pub fn client_from_session(session: &Session) -> Option<ResourceServiceClient> {
    let channel = session.channel.lock().unwrap().clone()?;
    Some(DashboardServiceClient::with_interceptor(
        channel,
        build_interceptor(session),
    ))
}

/// Records a session's connection state and pushes it to the UI when the session
/// is active. Storing it lets the UI be re-primed with the correct state when the
/// user switches to this AppHost.
fn set_connection(app: &AppHandle, state: &AppState, session: &Session, conn_state: &str, message: Option<String>) {
    let status = ConnectionStatus::new("resourceService", conn_state, message);
    *session.connection.lock().unwrap() = status.clone();
    if state.is_active(&session.id) {
        let _ = app.emit("deck://connection", &status);
    }
    // The switcher shows each AppHost's connection state, so refresh it too.
    state.emit_apphosts();
}

/// The long-running task that maintains one AppHost's resource service connection,
/// watches for resource changes, and pushes them to the UI while the session is
/// active. Reconnects with a fixed backoff when the stream drops.
pub async fn run_session_loop(app: AppHandle, state: Arc<AppState>, session: Arc<Session>) {
    let url = session.resource_service_url.clone();
    let mut is_reconnect = false;

    loop {
        set_connection(&app, &state, &session, "connecting", Some(url.clone()));

        let channel = match connect_channel(&url).await {
            Ok(channel) => channel,
            Err(err) => {
                set_connection(&app, &state, &session, "error", Some(err));
                tokio::time::sleep(RECONNECT_DELAY).await;
                is_reconnect = true;
                continue;
            }
        };

        *session.channel.lock().unwrap() = Some(channel.clone());

        let mut client =
            DashboardServiceClient::with_interceptor(channel, build_interceptor(&session));

        // Fetch the application name (best effort).
        if let Ok(info) = client
            .get_application_information(ApplicationInformationRequest {})
            .await
        {
            let name = info.into_inner().application_name;
            *session.application_name.lock().unwrap() = Some(name);
            // The name feeds the switcher label.
            state.emit_apphosts();
        }

        set_connection(&app, &state, &session, "connected", Some(url.clone()));

        if let Err(err) = watch_resources(&app, &state, &session, &mut client, is_reconnect).await {
            set_connection(&app, &state, &session, "disconnected", Some(err));
        } else {
            set_connection(&app, &state, &session, "disconnected", None);
        }

        // Drop the cached channel so command handlers don't use a dead one.
        *session.channel.lock().unwrap() = None;
        tokio::time::sleep(RECONNECT_DELAY).await;
        is_reconnect = true;
    }
}

async fn watch_resources(
    app: &AppHandle,
    state: &AppState,
    session: &Session,
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
                    let mut map = session.resources.lock().unwrap();
                    map.clear();
                    for resource in &resources {
                        map.insert(resource.name.clone(), resource.clone());
                    }
                }
                if state.is_active(&session.id) {
                    let _ = app.emit("deck://resources", &ResourcesEvent::snapshot(resources));
                }
            }
            Some(watch_resources_update::Kind::Changes(changes)) => {
                let mut upserts = Vec::new();
                let mut deletes = Vec::new();
                {
                    let mut map = session.resources.lock().unwrap();
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
                if state.is_active(&session.id) && (!upserts.is_empty() || !deletes.is_empty()) {
                    let _ = app.emit("deck://resources", &ResourcesEvent::change(upserts, deletes));
                }
            }
            None => {}
        }
    }

    Ok(())
}

/// Streams console logs for a resource on the given session until the stream
/// completes or the task is aborted (via unsubscribe). Each batch of lines is
/// emitted on `deck://console-log`.
pub async fn stream_console_logs(app: AppHandle, session: Arc<Session>, resource_name: String) {
    let mut client = match client_from_session(&session) {
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

/// Executes a command against a resource on the given session.
// `parameter` and `error_message` are deprecated in the proto but still part of
// the generated struct/response; we set/read them for wire compatibility.
#[allow(deprecated)]
pub async fn execute_command(
    session: Arc<Session>,
    resource_name: String,
    resource_type: String,
    command_name: String,
) -> CommandResponse {
    let mut client = match client_from_session(&session) {
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
        // Run interactively (matching the dashboard). When a command declares argument
        // inputs (e.g. set-parameter) or its callback calls IInteractionService.Prompt*,
        // the AppHost raises an inputs dialog over the WatchInteractions stream, which
        // Deck renders as the interaction pane. Sending non_interactive=true would instead
        // make the AppHost skip the prompt and immediately fail required-argument commands
        // with "Command argument validation failed."
        non_interactive: false,
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
