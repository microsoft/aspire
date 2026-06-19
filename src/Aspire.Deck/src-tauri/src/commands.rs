//! Tauri command handlers — the request/response half of the UI boundary.
//! See `../CONTRACT.md`.

use std::sync::Arc;

use tauri::{AppHandle, State};

use crate::canvas::CanvasManifest;
use crate::config::DeckConfigView;
use crate::model::{CommandResponse, Resource};
use crate::otlp::TelemetrySummary;
use crate::resource_client;
use crate::state::AppState;

#[tauri::command]
pub fn deck_get_config(state: State<'_, Arc<AppState>>) -> DeckConfigView {
    let application_name = state.application_name.lock().unwrap().clone();
    state.config.view(application_name)
}

#[tauri::command]
pub fn deck_list_resources(state: State<'_, Arc<AppState>>) -> Vec<Resource> {
    state.resources.lock().unwrap().values().cloned().collect()
}

#[tauri::command]
pub fn deck_list_canvases(state: State<'_, Arc<AppState>>) -> Vec<CanvasManifest> {
    state.canvases.clone()
}

#[tauri::command]
pub fn deck_get_telemetry_summary(state: State<'_, Arc<AppState>>) -> TelemetrySummary {
    state.otlp.store.lock().unwrap().summary()
}

#[tauri::command]
pub fn deck_subscribe_console_logs(
    app: AppHandle,
    state: State<'_, Arc<AppState>>,
    resource_name: String,
) {
    let state = state.inner().clone();

    // Replace any existing subscription for this resource.
    {
        let mut tasks = state.console_tasks.lock().unwrap();
        if let Some(existing) = tasks.remove(&resource_name) {
            existing.abort();
        }
    }

    let task_state = state.clone();
    let task_app = app.clone();
    let name = resource_name.clone();
    let handle = tauri::async_runtime::spawn(async move {
        resource_client::stream_console_logs(task_app, task_state, name).await;
    });

    state
        .console_tasks
        .lock()
        .unwrap()
        .insert(resource_name, handle);
}

#[tauri::command]
pub fn deck_unsubscribe_console_logs(state: State<'_, Arc<AppState>>, resource_name: String) {
    if let Some(handle) = state.console_tasks.lock().unwrap().remove(&resource_name) {
        handle.abort();
    }
}

#[tauri::command]
pub async fn deck_execute_command(
    state: State<'_, Arc<AppState>>,
    resource_name: String,
    resource_type: String,
    command_name: String,
) -> Result<CommandResponse, String> {
    let state = state.inner().clone();
    Ok(resource_client::execute_command(state, resource_name, resource_type, command_name).await)
}

#[tauri::command]
pub fn deck_open_external(url: String) -> Result<(), String> {
    open::that(url).map_err(|e| e.to_string())
}
