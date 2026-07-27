//! Shared application state, managed by Tauri and accessed from command handlers
//! and background tasks.
//!
//! Aspire Deck can attach to multiple AppHosts at once (each `aspire run --deck`
//! registers one). Each attached AppHost is a [`Session`] with its own resource
//! service connection. The UI shows one session at a time — the *active* session —
//! and can switch between them. OTLP telemetry is currently shared across sessions
//! (a single ingestion store); per-session telemetry isolation is a follow-up.

use std::collections::HashMap;
use std::sync::{Arc, Mutex};

use indexmap::IndexMap;
use tauri::async_runtime::JoinHandle;
use tauri::{AppHandle, Emitter};
use tonic::transport::Channel;

use crate::canvas::CanvasManifest;
use crate::config::{AuthMode, DeckConfig};
use crate::model::{AppHostInfo, ConnectionStatus, Resource, ResourcesEvent};
use crate::otlp::{OtlpShared, TelemetryStore};

/// A single attached AppHost: one resource-service connection, its resources, and
/// any active console-log streams.
pub struct Session {
    pub id: String,
    pub resource_service_url: String,
    pub auth_mode: AuthMode,
    pub api_key: Option<String>,
    /// The application name reported by the resource service, if connected.
    pub application_name: Mutex<Option<String>>,
    /// The current resource snapshot, keyed by resource name. `IndexMap` preserves
    /// insertion order so the UI sees a stable ordering.
    pub resources: Mutex<IndexMap<String, Resource>>,
    /// The currently-connected resource service channel (cloneable and cheap).
    pub channel: Mutex<Option<Channel>>,
    /// Active console-log streaming tasks, keyed by resource name, so they can be
    /// aborted on unsubscribe or when the session is removed.
    pub console_tasks: Mutex<HashMap<String, JoinHandle<()>>>,
    /// The resource-watch loop task, so it can be aborted when the session is removed.
    pub loop_handle: Mutex<Option<JoinHandle<()>>>,
    /// Last known connection state, re-emitted to the UI when this session becomes active.
    pub connection: Mutex<ConnectionStatus>,
    /// Sender for the bidi WatchInteractions stream, used to reply to interaction
    /// prompts (command inputs, message boxes). Present while the stream is connected.
    pub interaction_tx: Mutex<Option<tokio::sync::mpsc::Sender<crate::proto::aspire::v1::WatchInteractionsRequestUpdate>>>,
    /// The interactions currently open for this AppHost, keyed by interaction id and
    /// kept in arrival order. An AppHost can have several open at once (e.g. multiple
    /// notifications plus an inputs dialog), so they are tracked as a map rather than a
    /// single slot. Kept so the UI can be re-primed on session switch and so responses
    /// can be rebuilt from the server's last copy of each interaction.
    pub interactions: Mutex<IndexMap<i32, crate::proto::aspire::v1::WatchInteractionsResponseUpdate>>,
    /// The interaction-watch loop task.
    pub interaction_handle: Mutex<Option<JoinHandle<()>>>,
    /// This AppHost's own telemetry (metrics, traces, structured logs). Telemetry
    /// is partitioned per AppHost — OTLP records are attributed to the session that
    /// owns the resource by `service.name` — so switching AppHosts shows only the
    /// active one's telemetry instead of a merged firehose.
    pub telemetry: Mutex<TelemetryStore>,
}

impl Session {
    pub fn new(id: String, resource_service_url: String, auth_mode: AuthMode, api_key: Option<String>) -> Self {
        Session {
            id,
            resource_service_url,
            auth_mode,
            api_key,
            application_name: Mutex::new(None),
            resources: Mutex::new(IndexMap::new()),
            channel: Mutex::new(None),
            console_tasks: Mutex::new(HashMap::new()),
            loop_handle: Mutex::new(None),
            connection: Mutex::new(ConnectionStatus::new("resourceService", "connecting", None)),
            interaction_tx: Mutex::new(None),
            interactions: Mutex::new(IndexMap::new()),
            interaction_handle: Mutex::new(None),
            telemetry: Mutex::new(TelemetryStore::new()),
        }
    }

    /// Aborts the resource loop and any console streams. Called when the AppHost
    /// is unregistered (e.g. the run ends).
    pub fn shutdown(&self) {
        if let Some(handle) = self.loop_handle.lock().unwrap().take() {
            handle.abort();
        }
        if let Some(handle) = self.interaction_handle.lock().unwrap().take() {
            handle.abort();
        }
        let mut tasks = self.console_tasks.lock().unwrap();
        for (_, handle) in tasks.drain() {
            handle.abort();
        }
    }
}

pub struct AppState {
    pub config: DeckConfig,
    /// Shared OTLP telemetry store and emitter.
    pub otlp: Arc<OtlpShared>,
    /// Canvases discovered at startup.
    pub canvases: Vec<CanvasManifest>,
    /// The Tauri app handle, used to spawn session loops and emit events from the
    /// control server (which runs outside the Tauri command context).
    pub app: AppHandle,
    /// Attached AppHosts, keyed by id, in registration order.
    pub sessions: Mutex<IndexMap<String, Arc<Session>>>,
    /// The id of the active session (the one the UI is showing), if any.
    pub active: Mutex<Option<String>>,
}

impl AppState {
    pub fn new(config: DeckConfig, otlp: Arc<OtlpShared>, canvases: Vec<CanvasManifest>, app: AppHandle) -> Self {
        AppState {
            config,
            otlp,
            canvases,
            app,
            sessions: Mutex::new(IndexMap::new()),
            active: Mutex::new(None),
        }
    }

    /// Returns the active session, if any.
    pub fn active_session(&self) -> Option<Arc<Session>> {
        let active = self.active.lock().unwrap().clone()?;
        self.sessions.lock().unwrap().get(&active).cloned()
    }

    /// Returns true if the given session id is the active one.
    pub fn is_active(&self, id: &str) -> bool {
        self.active.lock().unwrap().as_deref() == Some(id)
    }

    /// Returns the list of attached AppHosts for the UI switcher.
    pub fn apphost_list(&self) -> Vec<AppHostInfo> {
        let active = self.active.lock().unwrap().clone();
        self.sessions
            .lock()
            .unwrap()
            .values()
            .map(|s| AppHostInfo {
                id: s.id.clone(),
                name: s
                    .application_name
                    .lock()
                    .unwrap()
                    .clone()
                    .unwrap_or_else(|| s.id.clone()),
                resource_service_url: s.resource_service_url.clone(),
                state: s.connection.lock().unwrap().state.clone(),
                active: active.as_deref() == Some(s.id.as_str()),
            })
            .collect()
    }

    /// Emits the current AppHost list to the UI.
    pub fn emit_apphosts(&self) {
        let _ = self.app.emit("deck://apphosts", &self.apphost_list());
    }

    /// Registers (attaches) an AppHost: creates a session, starts its resource
    /// loop, and makes it active if it is the first one. Re-registering the same
    /// id replaces the previous session (e.g. an AppHost restart). `name` is an
    /// initial display label; the resource service's application name overrides it
    /// once connected.
    pub fn register_session(self: &Arc<Self>, id: String, name: Option<String>, url: String, auth_mode: AuthMode, api_key: Option<String>) {
        let session = Arc::new(Session::new(id.clone(), url, auth_mode, api_key));
        if name.is_some() {
            *session.application_name.lock().unwrap() = name;
        }
        {
            let mut sessions = self.sessions.lock().unwrap();
            if let Some(existing) = sessions.get(&id) {
                existing.shutdown();
            }
            sessions.insert(id.clone(), session.clone());
        }

        // The newly attached AppHost becomes the active one, so `aspire run --deck`
        // opens Deck showing the app you just ran (and a fresh attach brings it to
        // the front).
        {
            let mut active = self.active.lock().unwrap();
            *active = Some(id.clone());
        }

        let loop_app = self.app.clone();
        let loop_state = self.clone();
        let loop_session = session.clone();
        let handle = tauri::async_runtime::spawn(async move {
            crate::resource_client::run_session_loop(loop_app, loop_state, loop_session).await;
        });
        *session.loop_handle.lock().unwrap() = Some(handle);

        // Also watch the interaction stream so command-input prompts surface in the UI.
        let int_app = self.app.clone();
        let int_state = self.clone();
        let int_session = session.clone();
        let int_handle = tauri::async_runtime::spawn(async move {
            crate::interaction::run_interaction_loop(int_app, int_state, int_session).await;
        });
        *session.interaction_handle.lock().unwrap() = Some(int_handle);

        self.emit_apphosts();
        self.emit_active_snapshot();
    }

    /// Unregisters (detaches) an AppHost, aborting its loop. If it was active, the
    /// next attached AppHost (if any) becomes active.
    pub fn unregister_session(self: &Arc<Self>, id: &str) {
        let removed = { self.sessions.lock().unwrap().shift_remove(id) };
        if let Some(session) = removed {
            session.shutdown();
        }

        let was_active = { self.active.lock().unwrap().as_deref() == Some(id) };
        if was_active {
            let next = self.sessions.lock().unwrap().keys().next().cloned();
            *self.active.lock().unwrap() = next;
            self.emit_active_snapshot();
        }

        self.emit_apphosts();
    }

    /// Switches the active AppHost and re-primes the UI with its current state.
    pub fn set_active(self: &Arc<Self>, id: &str) {
        {
            let mut active = self.active.lock().unwrap();
            *active = Some(id.to_string());
        }
        self.emit_active_snapshot();
        self.emit_apphosts();
    }

    /// Emits the active session's connection + resource snapshot so the UI reflects
    /// the current AppHost (used after a switch or when the active session changes).
    fn emit_active_snapshot(&self) {
        if let Some(session) = self.active_session() {
            let conn = session.connection.lock().unwrap().clone();
            let _ = self.app.emit("deck://connection", &conn);
            let resources: Vec<Resource> =
                session.resources.lock().unwrap().values().cloned().collect();
            let _ = self.app.emit("deck://resources", &ResourcesEvent::snapshot(resources));
            // Re-prime any in-flight interaction for the newly active AppHost.
            crate::interaction::emit_active_interaction(&self.app, &session);
        } else {
            // No AppHost attached — show a disconnected, empty state.
            let _ = self
                .app
                .emit("deck://connection", &ConnectionStatus::new("resourceService", "disconnected", None));
            let _ = self.app.emit("deck://resources", &ResourcesEvent::snapshot(vec![]));
            let _ = self
                .app
                .emit("deck://interactions", &Vec::<crate::model::InteractionEvent>::new());
        }
        // Telemetry is per-AppHost, so a switch must reset the UI to the active
        // AppHost's metrics/traces/logs (or clear them when none is attached).
        self.emit_active_telemetry();
    }

    /// Emits the active AppHost's telemetry summary (or an empty one when none is
    /// attached). Called on switch and by the debounced OTLP emitter.
    pub fn emit_active_telemetry(&self) {
        let summary = match self.active_session() {
            Some(session) => session.telemetry.lock().unwrap().summary(),
            None => crate::otlp::TelemetrySummary::empty(),
        };
        let _ = self.app.emit("deck://telemetry", &summary);
    }
}
