//! Control plane for Aspire Deck.
//!
//! Stage 2 makes Deck a persistent app that multiple AppHosts attach to. Each
//! `aspire run --deck` finds a running Deck (via the instance file) and registers
//! its AppHost over this small loopback HTTP API instead of launching a new Deck.
//!
//! Discovery: Deck writes `<home>/.aspire/deck/instance.json` describing its
//! control endpoint, shared OTLP endpoints, a registration token, and its PID. The
//! CLI reads this file, verifies the process is alive, and registers/unregisters
//! AppHosts. The token gates registration so other local processes can't attach.

use std::io::Write;
use std::net::SocketAddr;
use std::path::PathBuf;
use std::sync::Arc;

use serde::{Deserialize, Serialize};
use tauri::Manager;

use crate::config::AuthMode;
use crate::state::AppState;

/// The instance file describing a running Deck, read by the CLI.
#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct InstanceFile {
    pub control_url: String,
    pub otlp_grpc_url: String,
    pub otlp_http_url: String,
    /// Token a registrant must present (header `x-deck-token`) to attach an AppHost.
    pub token: String,
    pub pid: u32,
}

/// Registration request body sent by `aspire run --deck`.
#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase")]
struct RegisterRequest {
    /// Stable id for the AppHost (the CLI assigns one per run).
    id: String,
    /// Optional display name; the resource service's application name overrides it.
    #[serde(default)]
    name: Option<String>,
    resource_service_url: String,
    /// Optional resource-service API key. When present, API-key auth is used.
    #[serde(default)]
    api_key: Option<String>,
}

#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase")]
struct UnregisterRequest {
    id: String,
}

/// `<home>/.aspire/deck/instance.json`. Both Deck (writer) and the CLI (reader)
/// compute this path the same way.
pub fn instance_file_path() -> Option<PathBuf> {
    let home = std::env::var("HOME")
        .ok()
        .or_else(|| std::env::var("USERPROFILE").ok())?;
    Some(PathBuf::from(home).join(".aspire").join("deck").join("instance.json"))
}

fn write_instance_file(file: &InstanceFile) -> std::io::Result<()> {
    let path = instance_file_path()
        .ok_or_else(|| std::io::Error::new(std::io::ErrorKind::NotFound, "no home directory"))?;
    if let Some(parent) = path.parent() {
        std::fs::create_dir_all(parent)?;
    }
    let json = serde_json::to_string_pretty(file)
        .map_err(|e| std::io::Error::new(std::io::ErrorKind::InvalidData, e))?;
    let mut f = std::fs::File::create(&path)?;
    f.write_all(json.as_bytes())?;
    Ok(())
}

/// Removes the instance file (best effort), e.g. on shutdown.
pub fn remove_instance_file() {
    if let Some(path) = instance_file_path() {
        let _ = std::fs::remove_file(path);
    }
}

/// Starts the control HTTP server on a free loopback port and writes the instance
/// file so the CLI can discover this Deck. `otlp_grpc_url`/`otlp_http_url` are the
/// shared OTLP endpoints this Deck hosts (AppHosts are pointed at them).
pub fn start(state: Arc<AppState>, otlp_grpc_url: String, otlp_http_url: String) {
    use axum::extract::State;
    use axum::http::{HeaderMap, StatusCode};
    use axum::routing::{get, post};
    use axum::{Json, Router};

    let token = uuid::Uuid::new_v4().to_string();

    fn check_token(headers: &HeaderMap, expected: &str) -> bool {
        headers
            .get("x-deck-token")
            .and_then(|v| v.to_str().ok())
            .map(|v| v == expected)
            .unwrap_or(false)
    }

    async fn health() -> StatusCode {
        StatusCode::OK
    }

    // Lists attached AppHosts (also handy for diagnostics).
    async fn apphosts(State(state): State<Arc<AppState>>) -> axum::Json<Vec<crate::model::AppHostInfo>> {
        axum::Json(state.apphost_list())
    }

    // Brings Deck's window to the front. `aspire deck` calls this when a hub is
    // already running so it focuses the existing window instead of opening a second.
    let focus_token = token.clone();
    let focus = move |State(state): State<Arc<AppState>>, headers: HeaderMap| {
        let token = focus_token.clone();
        async move {
            if !check_token(&headers, &token) {
                return StatusCode::UNAUTHORIZED;
            }
            if let Some(window) = state.app.get_webview_window("main") {
                let _ = window.unminimize();
                let _ = window.show();
                let _ = window.set_focus();
            }
            StatusCode::OK
        }
    };

    let register_token = token.clone();
    let register = move |State(state): State<Arc<AppState>>, headers: HeaderMap, Json(body): Json<RegisterRequest>| {
        let token = register_token.clone();
        async move {
            if !check_token(&headers, &token) {
                return StatusCode::UNAUTHORIZED;
            }
            let auth_mode = if body.api_key.is_some() {
                AuthMode::ApiKey
            } else {
                AuthMode::Unsecured
            };
            state.register_session(body.id, body.name, body.resource_service_url, auth_mode, body.api_key);
            StatusCode::OK
        }
    };

    let unregister_token = token.clone();
    let unregister = move |State(state): State<Arc<AppState>>, headers: HeaderMap, Json(body): Json<UnregisterRequest>| {
        let token = unregister_token.clone();
        async move {
            if !check_token(&headers, &token) {
                return StatusCode::UNAUTHORIZED;
            }
            state.unregister_session(&body.id);
            StatusCode::OK
        }
    };

    let router = Router::new()
        .route("/health", get(health))
        .route("/apphosts", get(apphosts))
        .route("/focus", post(focus))
        .route("/register", post(register))
        .route("/unregister", post(unregister))
        .with_state(state.clone());

    tauri::async_runtime::spawn(async move {
        // Bind to an OS-assigned loopback port.
        let addr = SocketAddr::from(([127, 0, 0, 1], 0));
        let listener = match tokio::net::TcpListener::bind(addr).await {
            Ok(listener) => listener,
            Err(err) => {
                tracing::error!("Aspire Deck control server failed to bind: {err}");
                return;
            }
        };

        let local_addr = match listener.local_addr() {
            Ok(addr) => addr,
            Err(err) => {
                tracing::error!("Aspire Deck control server has no local address: {err}");
                return;
            }
        };

        let instance = InstanceFile {
            control_url: format!("http://127.0.0.1:{}", local_addr.port()),
            otlp_grpc_url,
            otlp_http_url,
            token,
            pid: std::process::id(),
        };

        if let Err(err) = write_instance_file(&instance) {
            tracing::warn!("Failed to write Aspire Deck instance file: {err}");
        } else {
            tracing::info!("Aspire Deck control endpoint: {}", instance.control_url);
        }

        if let Err(err) = axum::serve(listener, router).await {
            tracing::error!("Aspire Deck control server error: {err}");
        }
    });
}
