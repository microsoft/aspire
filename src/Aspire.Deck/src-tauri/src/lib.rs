//! Aspire Deck — a native dashboard for .NET Aspire.
//!
//! This is the Tauri application library. The Rust side hosts the OTLP ingestion
//! servers and the resource-service gRPC client (so the Deck is a drop-in
//! replacement for the Blazor dashboard's IPC), and pushes data to the native UI
//! via Tauri events. See `../CONTRACT.md` for the UI boundary.

mod canvas;
mod commands;
mod config;
mod model;
mod otlp;
mod proto;
mod resource_client;
mod state;

use std::sync::{Arc, Mutex};

use tauri::Manager;

use crate::config::DeckConfig;
use crate::otlp::{OtlpAuth, OtlpShared, TelemetryStore};
use crate::state::AppState;

#[cfg_attr(mobile, tauri::mobile_entry_point)]
pub fn run() {
    // Best-effort tracing; ignore failures (e.g. if a global subscriber is set).
    let _ = tracing_subscriber::fmt()
        .with_env_filter(
            tracing_subscriber::EnvFilter::try_from_default_env()
                .unwrap_or_else(|_| tracing_subscriber::EnvFilter::new("info")),
        )
        .try_init();

    let config = DeckConfig::from_env();
    let canvases = canvas::discover();

    tauri::Builder::default()
        .setup(move |app| {
            let app_handle = app.handle().clone();

            let otlp_shared = Arc::new(OtlpShared {
                store: Mutex::new(TelemetryStore::new()),
                app: app_handle.clone(),
                auth: OtlpAuth::from_config(&config.otlp),
            });

            // Start OTLP ingestion servers.
            otlp::start(app_handle.clone(), &config.otlp, otlp_shared.clone());

            let state = Arc::new(AppState::new(config.clone(), otlp_shared, canvases.clone()));
            app.manage(state.clone());

            // Start the resource-service watch loop.
            let loop_app = app_handle.clone();
            tauri::async_runtime::spawn(async move {
                resource_client::run_resource_loop(loop_app, state).await;
            });

            Ok(())
        })
        .invoke_handler(tauri::generate_handler![
            commands::deck_get_config,
            commands::deck_list_resources,
            commands::deck_list_canvases,
            commands::deck_get_telemetry_summary,
            commands::deck_subscribe_console_logs,
            commands::deck_unsubscribe_console_logs,
            commands::deck_execute_command,
            commands::deck_open_external,
        ])
        .run(tauri::generate_context!())
        .expect("error while running Aspire Deck");
}
