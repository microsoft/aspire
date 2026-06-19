//! Shared application state, managed by Tauri and accessed from command handlers
//! and background tasks.

use std::collections::HashMap;
use std::sync::{Arc, Mutex};

use indexmap::IndexMap;
use tauri::async_runtime::JoinHandle;
use tonic::transport::Channel;

use crate::canvas::CanvasManifest;
use crate::config::DeckConfig;
use crate::model::Resource;
use crate::otlp::OtlpShared;

pub struct AppState {
    pub config: DeckConfig,
    /// The application name reported by the resource service, if connected.
    pub application_name: Mutex<Option<String>>,
    /// The current resource snapshot, keyed by resource name. `IndexMap`
    /// preserves insertion order so the UI sees a stable ordering.
    pub resources: Mutex<IndexMap<String, Resource>>,
    /// Shared OTLP telemetry store and emitter.
    pub otlp: Arc<OtlpShared>,
    /// The currently-connected resource service channel (cloneable and cheap).
    pub channel: Mutex<Option<Channel>>,
    /// Active console-log streaming tasks, keyed by resource name, so they can be
    /// aborted on unsubscribe.
    pub console_tasks: Mutex<HashMap<String, JoinHandle<()>>>,
    /// Canvases discovered at startup.
    pub canvases: Vec<CanvasManifest>,
}

impl AppState {
    pub fn new(config: DeckConfig, otlp: Arc<OtlpShared>, canvases: Vec<CanvasManifest>) -> Self {
        AppState {
            config,
            application_name: Mutex::new(None),
            resources: Mutex::new(IndexMap::new()),
            otlp,
            channel: Mutex::new(None),
            console_tasks: Mutex::new(HashMap::new()),
            canvases,
        }
    }
}
