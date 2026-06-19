//! Aspire Deck — a native dashboard for .NET Aspire.
//!
//! This is the Tauri application library. The Rust side hosts the OTLP ingestion
//! servers and the resource-service gRPC client (so the Deck is a drop-in
//! replacement for the Blazor dashboard's IPC), and pushes data to the native UI
//! via Tauri events. See `../CONTRACT.md` for the UI boundary.
//!
//! Deck can attach to multiple AppHosts at once. The first is taken from the
//! environment (when launched by `aspire deck` or the first `aspire run --deck`);
//! additional AppHosts register over the control server (see `control.rs`).

mod canvas;
mod commands;
mod config;
mod control;
mod model;
mod otlp;
mod proto;
mod resource_client;
mod state;

use std::sync::{Arc, Mutex};

use tauri::Manager;

use crate::config::{DeckConfig, AuthMode};
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

            // Start OTLP ingestion servers (shared across all attached AppHosts).
            otlp::start(app_handle.clone(), &config.otlp, otlp_shared.clone());

            let state = Arc::new(AppState::new(
                config.clone(),
                otlp_shared,
                canvases.clone(),
                app_handle.clone(),
            ));
            app.manage(state.clone());

            // Bootstrap the AppHost provided via the environment (e.g. `aspire deck`
            // or the first `aspire run --deck`). Additional AppHosts attach later
            // over the control server.
            if let Some(url) = config.resource_service.url.clone() {
                let api_key = if config.resource_service.auth_mode == AuthMode::ApiKey {
                    config.resource_service.api_key.clone()
                } else {
                    None
                };
                state.register_session(
                    "local".to_string(),
                    None,
                    url,
                    config.resource_service.auth_mode.clone(),
                    api_key,
                );
            }

            // Start the control server so other `aspire run --deck` invocations can
            // attach their AppHosts to this Deck instance.
            control::start(
                state.clone(),
                config.otlp.grpc_url.clone(),
                config.otlp.http_url.clone(),
            );

            Ok(())
        })
        .invoke_handler(tauri::generate_handler![
            commands::deck_get_config,
            commands::deck_list_resources,
            commands::deck_list_apphosts,
            commands::deck_select_apphost,
            commands::deck_list_canvases,
            commands::deck_get_telemetry_summary,
            commands::deck_subscribe_console_logs,
            commands::deck_unsubscribe_console_logs,
            commands::deck_execute_command,
            commands::deck_open_external,
        ])
        .build(tauri::generate_context!())
        .expect("error while building Aspire Deck")
        .run(|_app_handle, event| {
            // Remove the discovery file when Deck exits so the CLI doesn't try to
            // attach to a dead instance (it also verifies the PID, but cleaning up
            // avoids a stale-file round trip).
            if let tauri::RunEvent::Exit = event {
                control::remove_instance_file();
            }
        });
}
