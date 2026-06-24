//! Tauri command handlers — the request/response half of the UI boundary.
//! See `../CONTRACT.md`.
//!
//! Resource/console/command operations target the *active* AppHost session. The
//! AppHost switcher commands (`deck_list_apphosts`/`deck_select_apphost`) change
//! which session is active.

use std::sync::Arc;

use tauri::{AppHandle, State};

use crate::canvas::CanvasManifest;
use crate::config::DeckConfigView;
use crate::model::{AppHostInfo, CommandResponse, Resource};
use crate::otlp::{MetricSeriesResponse, TelemetrySummary};
use crate::resource_client;
use crate::state::AppState;

#[tauri::command]
pub fn deck_get_config(state: State<'_, Arc<AppState>>) -> DeckConfigView {
    let application_name = state
        .active_session()
        .and_then(|s| s.application_name.lock().unwrap().clone());
    state.config.view(application_name)
}

#[tauri::command]
pub fn deck_list_resources(state: State<'_, Arc<AppState>>) -> Vec<Resource> {
    match state.active_session() {
        Some(session) => session.resources.lock().unwrap().values().cloned().collect(),
        None => Vec::new(),
    }
}

#[tauri::command]
pub fn deck_list_apphosts(state: State<'_, Arc<AppState>>) -> Vec<AppHostInfo> {
    state.apphost_list()
}

#[tauri::command]
pub fn deck_select_apphost(state: State<'_, Arc<AppState>>, id: String) {
    let state = state.inner().clone();
    // Only switch to a known AppHost.
    if state.sessions.lock().unwrap().contains_key(&id) {
        state.set_active(&id);
    }
}

#[tauri::command]
pub fn deck_list_canvases(state: State<'_, Arc<AppState>>) -> Vec<CanvasManifest> {
    state.canvases.clone()
}

#[tauri::command]
pub fn deck_get_telemetry_summary(state: State<'_, Arc<AppState>>) -> TelemetrySummary {
    state.otlp.store.lock().unwrap().summary()
}

/// Returns the downsampled time series for a metric within the requested window.
/// `resourceName` disambiguates when several resources emit the same metric name;
/// when omitted, the first matching series is returned.
#[tauri::command]
pub fn deck_get_metric_series(
    state: State<'_, Arc<AppState>>,
    name: String,
    resource_name: Option<String>,
    window_seconds: Option<u64>,
    max_points: Option<usize>,
) -> Option<MetricSeriesResponse> {
    let window_ms = window_seconds.unwrap_or(300) as i64 * 1000;
    let max_points = max_points.unwrap_or(400).clamp(2, 4000);
    state
        .otlp
        .store
        .lock()
        .unwrap()
        .query_metric(&name, resource_name.as_deref(), window_ms, max_points)
}

#[tauri::command]
pub fn deck_subscribe_console_logs(
    app: AppHandle,
    state: State<'_, Arc<AppState>>,
    resource_name: String,
) {
    let session = match state.active_session() {
        Some(session) => session,
        None => return,
    };

    // Replace any existing subscription for this resource.
    {
        let mut tasks = session.console_tasks.lock().unwrap();
        if let Some(existing) = tasks.remove(&resource_name) {
            existing.abort();
        }
    }

    let task_session = session.clone();
    let task_app = app.clone();
    let name = resource_name.clone();
    let handle = tauri::async_runtime::spawn(async move {
        resource_client::stream_console_logs(task_app, task_session, name).await;
    });

    session
        .console_tasks
        .lock()
        .unwrap()
        .insert(resource_name, handle);
}

#[tauri::command]
pub fn deck_unsubscribe_console_logs(state: State<'_, Arc<AppState>>, resource_name: String) {
    if let Some(session) = state.active_session() {
        if let Some(handle) = session.console_tasks.lock().unwrap().remove(&resource_name) {
            handle.abort();
        }
    }
}

#[tauri::command]
pub async fn deck_execute_command(
    state: State<'_, Arc<AppState>>,
    resource_name: String,
    resource_type: String,
    command_name: String,
) -> Result<CommandResponse, String> {
    let session = match state.active_session() {
        Some(session) => session,
        None => {
            return Ok(CommandResponse {
                kind: "failed".to_string(),
                message: Some("No AppHost is attached".to_string()),
            })
        }
    };
    Ok(resource_client::execute_command(session, resource_name, resource_type, command_name).await)
}

#[tauri::command]
pub fn deck_open_external(url: String) -> Result<(), String> {
    open::that(url).map_err(|e| e.to_string())
}

/// Replies to the active AppHost's current interaction (command-input dialog,
/// message box, or notification). `values` maps input names to their string
/// values (booleans as "true"/"false", choices as the option value).
#[tauri::command]
pub fn deck_respond_interaction(
    state: State<'_, Arc<AppState>>,
    action: String,
    values: std::collections::HashMap<String, String>,
) {
    if let Some(session) = state.active_session() {
        crate::interaction::respond(&session, &action, values);
    }
}
