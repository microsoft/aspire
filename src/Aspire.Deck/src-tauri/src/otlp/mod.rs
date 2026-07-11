//! OTLP ingestion. Aspire Deck hosts the same OpenTelemetry endpoints the Blazor
//! dashboard does, so telemetry exporters configured against the dashboard work
//! unchanged: a gRPC endpoint implementing the OTLP `Export` RPCs for traces,
//! metrics and logs, plus an HTTP endpoint exposing `/v1/{traces,metrics,logs}`.
//!
//! Ingested signals are summarized into an in-memory store (recent records are
//! capped) and pushed to the UI via the `deck://telemetry` event.

use std::collections::{HashMap, VecDeque};
use std::net::SocketAddr;
use std::sync::{Arc, Mutex};
use std::time::{Duration, Instant};

use serde::Serialize;
use tauri::{AppHandle, Emitter};
use tonic::transport::Server;
use tonic::{Request, Response, Status};

use crate::config::AuthMode;
use crate::model::ConnectionStatus;
use crate::proto::opentelemetry::proto::collector::logs::v1::logs_service_server::{
    LogsService, LogsServiceServer,
};
use crate::proto::opentelemetry::proto::collector::logs::v1::{
    ExportLogsServiceRequest, ExportLogsServiceResponse,
};
use crate::proto::opentelemetry::proto::collector::metrics::v1::metrics_service_server::{
    MetricsService, MetricsServiceServer,
};
use crate::proto::opentelemetry::proto::collector::metrics::v1::{
    ExportMetricsServiceRequest, ExportMetricsServiceResponse,
};
use crate::proto::opentelemetry::proto::collector::trace::v1::trace_service_server::{
    TraceService, TraceServiceServer,
};
use crate::proto::opentelemetry::proto::collector::trace::v1::{
    ExportTraceServiceRequest, ExportTraceServiceResponse,
};
use crate::proto::opentelemetry::proto::common::v1::{any_value, AnyValue, KeyValue};

pub mod metrics;
pub use metrics::{MetricKind, MetricSeriesResponse};

const RECENT_CAP: usize = 200;
const EMIT_DEBOUNCE: Duration = Duration::from_millis(400);

// ---------------------------------------------------------------------------
// Serializable summaries (see ../CONTRACT.md)
// ---------------------------------------------------------------------------

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct LogRecordSummary {
    pub time_unix_nano: String,
    pub severity: Option<String>,
    pub severity_number: i32,
    pub body: String,
    pub resource_name: Option<String>,
    pub trace_id: Option<String>,
    pub span_id: Option<String>,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct SpanSummary {
    pub trace_id: String,
    pub span_id: String,
    pub parent_span_id: Option<String>,
    pub name: String,
    pub kind: String,
    pub resource_name: Option<String>,
    pub start_unix_nano: String,
    pub duration_nanos: String,
    pub status_code: Option<String>,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct MetricSummary {
    pub name: String,
    pub unit: Option<String>,
    pub resource_name: Option<String>,
    pub kind: MetricKind,
    pub last_value: Option<f64>,
    pub point_count: u64,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct TelemetrySummary {
    pub log_count: u64,
    pub span_count: u64,
    pub metric_count: u64,
    pub recent_logs: Vec<LogRecordSummary>,
    pub recent_spans: Vec<SpanSummary>,
    pub metrics: Vec<MetricSummary>,
}

impl TelemetrySummary {
    /// An empty summary, emitted when no AppHost is active so the UI clears.
    pub fn empty() -> Self {
        TelemetrySummary {
            log_count: 0,
            span_count: 0,
            metric_count: 0,
            recent_logs: Vec::new(),
            recent_spans: Vec::new(),
            metrics: Vec::new(),
        }
    }
}

// ---------------------------------------------------------------------------
// Store
// ---------------------------------------------------------------------------

pub struct TelemetryStore {
    log_count: u64,
    log_count_by_resource: HashMap<Option<String>, u64>,
    span_count: u64,
    metric_count: u64,
    recent_logs: VecDeque<LogRecordSummary>,
    recent_spans: VecDeque<SpanSummary>,
    metrics: metrics::MetricStore,
}

impl TelemetryStore {
    pub fn new() -> Self {
        TelemetryStore {
            log_count: 0,
            log_count_by_resource: HashMap::new(),
            span_count: 0,
            metric_count: 0,
            recent_logs: VecDeque::with_capacity(RECENT_CAP),
            recent_spans: VecDeque::with_capacity(RECENT_CAP),
            metrics: metrics::MetricStore::default(),
        }
    }

    fn push_log(&mut self, log: LogRecordSummary) {
        self.log_count += 1;
        *self
            .log_count_by_resource
            .entry(log.resource_name.clone())
            .or_default() += 1;
        if self.recent_logs.len() >= RECENT_CAP {
            self.recent_logs.pop_back();
        }
        self.recent_logs.push_front(log);
    }

    pub fn clear_logs(&mut self, resource_name: Option<&str>) {
        if let Some(resource_name) = resource_name {
            let key = Some(resource_name.to_string());
            let removed_count = self.log_count_by_resource.remove(&key).unwrap_or_default();
            self.log_count = self.log_count.saturating_sub(removed_count);
            self.recent_logs
                .retain(|log| log.resource_name.as_deref() != Some(resource_name));
        } else {
            self.log_count = 0;
            self.log_count_by_resource.clear();
            self.recent_logs.clear();
        }
    }

    fn push_span(&mut self, span: SpanSummary) {
        self.span_count += 1;
        if self.recent_spans.len() >= RECENT_CAP {
            self.recent_spans.pop_back();
        }
        self.recent_spans.push_front(span);
    }

    /// Records one numeric (gauge/sum) data point into its series.
    fn record_number_point(
        &mut self,
        name: &str,
        unit: Option<String>,
        resource: Option<&str>,
        kind: MetricKind,
        t_ms: i64,
        value: f64,
        is_delta: bool,
    ) {
        self.metric_count += 1;
        self.metrics.point_count += 1;
        self.metrics
            .series_mut(name, resource, unit, kind)
            .record_number(t_ms, value, is_delta);
    }

    /// Records one histogram data point into its series.
    fn record_histogram_point(
        &mut self,
        name: &str,
        unit: Option<String>,
        resource: Option<&str>,
        t_ms: i64,
        bounds: &[f64],
        counts: &[u64],
        is_delta: bool,
    ) {
        self.metric_count += 1;
        self.metrics.point_count += 1;
        self.metrics
            .series_mut(name, resource, unit, MetricKind::Histogram)
            .record_histogram_point(t_ms, bounds, counts, is_delta);
    }

    /// Returns the time series for a metric, downsampled to the requested window.
    pub fn query_metric(
        &self,
        name: &str,
        resource: Option<&str>,
        window_ms: i64,
        max_points: usize,
    ) -> Option<MetricSeriesResponse> {
        self.metrics.get(name, resource).map(|s| s.query(window_ms, max_points))
    }

    pub fn summary(&self) -> TelemetrySummary {
        TelemetrySummary {
            log_count: self.log_count,
            span_count: self.span_count,
            metric_count: self.metric_count,
            recent_logs: self.recent_logs.iter().cloned().collect(),
            recent_spans: self.recent_spans.iter().cloned().collect(),
            // One summary row per (name, resource) series, for the metric list.
            metrics: self
                .metrics
                .iter()
                .map(|s| MetricSummary {
                    name: s.name.clone(),
                    unit: s.unit.clone(),
                    resource_name: s.resource_name.clone(),
                    kind: s.kind,
                    last_value: s.last_value(),
                    point_count: s.point_count(),
                })
                .collect(),
        }
    }
}

impl Default for TelemetryStore {
    fn default() -> Self {
        Self::new()
    }
}

#[cfg(test)]
mod tests {
    use super::{LogRecordSummary, TelemetryStore};

    fn log(resource_name: &str) -> LogRecordSummary {
        LogRecordSummary {
            time_unix_nano: "1".to_string(),
            severity: Some("Information".to_string()),
            severity_number: 9,
            body: "Test log".to_string(),
            resource_name: Some(resource_name.to_string()),
            trace_id: None,
            span_id: None,
        }
    }

    #[test]
    fn clear_logs_removes_selected_resource_or_all_logs() {
        let mut store = TelemetryStore::new();
        store.push_log(log("frontend"));
        store.push_log(log("frontend"));
        store.push_log(log("backend"));

        store.clear_logs(Some("frontend"));

        let selected_summary = store.summary();
        assert_eq!(selected_summary.log_count, 1);
        assert_eq!(selected_summary.recent_logs.len(), 1);
        assert_eq!(
            selected_summary.recent_logs[0].resource_name.as_deref(),
            Some("backend")
        );

        store.clear_logs(None);

        let empty_summary = store.summary();
        assert_eq!(empty_summary.log_count, 0);
        assert!(empty_summary.recent_logs.is_empty());
    }
}

// ---------------------------------------------------------------------------
// Auth
// ---------------------------------------------------------------------------

#[derive(Clone)]
pub struct OtlpAuth {
    mode: AuthMode,
    primary: Option<Vec<u8>>,
    secondary: Option<Vec<u8>>,
}

impl OtlpAuth {
    pub fn from_config(cfg: &crate::config::OtlpConfig) -> Self {
        OtlpAuth {
            mode: cfg.auth_mode.clone(),
            primary: cfg.primary_api_key.as_ref().map(|k| k.as_bytes().to_vec()),
            secondary: cfg.secondary_api_key.as_ref().map(|k| k.as_bytes().to_vec()),
        }
    }

    /// Validates a provided `x-otlp-api-key`. Returns true when auth is disabled
    /// or the key matches the primary/secondary key. Uses a constant-time
    /// comparison to avoid leaking key contents via timing.
    pub fn check(&self, provided: Option<&[u8]>) -> bool {
        if self.mode != AuthMode::ApiKey {
            return true;
        }
        let provided = match provided {
            Some(p) => p,
            None => return false,
        };
        for candidate in [self.primary.as_deref(), self.secondary.as_deref()]
            .into_iter()
            .flatten()
        {
            if constant_time_eq(candidate, provided) {
                return true;
            }
        }
        false
    }
}

fn constant_time_eq(a: &[u8], b: &[u8]) -> bool {
    if a.len() != b.len() {
        return false;
    }
    let mut diff = 0u8;
    for (x, y) in a.iter().zip(b.iter()) {
        diff |= x ^ y;
    }
    diff == 0
}

// ---------------------------------------------------------------------------
// Shared state passed to the OTLP servers
// ---------------------------------------------------------------------------

pub struct OtlpShared {
    pub app: AppHandle,
    pub auth: OtlpAuth,
    /// Back-reference to the app state, set after construction (AppState owns the
    /// Arc<OtlpShared>, so this is a Weak to avoid a reference cycle). Used to
    /// attribute incoming OTLP to the owning AppHost session and to emit the
    /// active session's telemetry.
    state: std::sync::OnceLock<std::sync::Weak<crate::state::AppState>>,
    /// Emit debounce: telemetry is partitioned per session, but UI emits are
    /// coalesced globally so a busy background AppHost can't spam the event.
    last_emit: Mutex<Option<Instant>>,
}

impl OtlpShared {
    pub fn new(app: AppHandle, auth: OtlpAuth) -> Self {
        OtlpShared {
            app,
            auth,
            state: std::sync::OnceLock::new(),
            last_emit: Mutex::new(None),
        }
    }

    /// Wires the back-reference to the app state. Called once at startup after the
    /// state is constructed.
    pub fn set_state(&self, state: &std::sync::Arc<crate::state::AppState>) {
        let _ = self.state.set(std::sync::Arc::downgrade(state));
    }

    fn app_state(&self) -> Option<std::sync::Arc<crate::state::AppState>> {
        self.state.get().and_then(|w| w.upgrade())
    }

    /// Resolves which AppHost session(s) a telemetry record with the given
    /// `service.name` belongs to. The OTLP `service.name` matches a resource name
    /// in exactly the AppHost that produced it (Aspire sets OTEL_SERVICE_NAME to
    /// the resource name; the dashboard keys OTLP the same way).
    ///
    /// - 0 or 1 attached AppHost: that session owns everything (fast path).
    /// - 2+ AppHosts: route only to the session(s) whose resources include
    ///   `service_name`. If none match, the record is dropped rather than
    ///   broadcast to every session.
    ///
    /// Dropping is deliberate: broadcasting unattributable telemetry to all
    /// sessions cross-contaminates their per-AppHost stores, which defeats the
    /// reset-on-switch guarantee. A record fails to match only when its owning
    /// AppHost's resource list has not loaded yet (a brief window right after
    /// attach) or when it carries no `service.name`. Aspire always sets
    /// `OTEL_SERVICE_NAME` to the resource name, so in practice the only casualty
    /// is a handful of points that arrive in the first moment after attach, which
    /// is a fair trade for clean isolation between AppHosts.
    fn target_sessions(&self, service_name: Option<&str>) -> Vec<std::sync::Arc<crate::state::Session>> {
        let Some(state) = self.app_state() else {
            return Vec::new();
        };
        let sessions: Vec<_> = state.sessions.lock().unwrap().values().cloned().collect();
        if sessions.len() <= 1 {
            return sessions;
        }
        let Some(name) = service_name else {
            return Vec::new();
        };
        sessions
            .into_iter()
            .filter(|s| s.resources.lock().unwrap().contains_key(name))
            .collect()
    }

    /// Emits a debounced `deck://telemetry` event for the *active* AppHost. We
    /// avoid emitting on every export under load by coalescing to at most one
    /// event per `EMIT_DEBOUNCE` window.
    fn maybe_emit(&self) {
        {
            let mut last = self.last_emit.lock().unwrap();
            let now = Instant::now();
            let should_emit = last.map_or(true, |l| now.duration_since(l) >= EMIT_DEBOUNCE);
            if !should_emit {
                return;
            }
            *last = Some(now);
        }
        if let Some(state) = self.app_state() {
            state.emit_active_telemetry();
        }
    }
}

// ---------------------------------------------------------------------------
// Attribute helpers
// ---------------------------------------------------------------------------

fn any_value_to_string(value: &AnyValue) -> String {
    match &value.value {
        Some(any_value::Value::StringValue(s)) => s.clone(),
        Some(any_value::Value::BoolValue(b)) => b.to_string(),
        Some(any_value::Value::IntValue(i)) => i.to_string(),
        Some(any_value::Value::DoubleValue(d)) => d.to_string(),
        Some(any_value::Value::BytesValue(b)) => format!("{} bytes", b.len()),
        Some(any_value::Value::ArrayValue(a)) => {
            let parts: Vec<String> = a.values.iter().map(any_value_to_string).collect();
            format!("[{}]", parts.join(", "))
        }
        Some(any_value::Value::KvlistValue(kv)) => {
            let parts: Vec<String> = kv
                .values
                .iter()
                .map(|k| format!("{}={}", k.key, k.value.as_ref().map(any_value_to_string).unwrap_or_default()))
                .collect();
            format!("{{{}}}", parts.join(", "))
        }
        None => String::new(),
    }
}

fn attribute(attributes: &[KeyValue], key: &str) -> Option<String> {
    attributes
        .iter()
        .find(|kv| kv.key == key)
        .and_then(|kv| kv.value.as_ref())
        .map(any_value_to_string)
}

/// Resolves the resource name used for display (the OTel `service.name`).
fn resource_name(resource: Option<&crate::proto::opentelemetry::proto::resource::v1::Resource>) -> Option<String> {
    resource.and_then(|r| attribute(&r.attributes, "service.name"))
}

fn severity_label(number: i32) -> Option<String> {
    use crate::proto::opentelemetry::proto::logs::v1::SeverityNumber;
    match SeverityNumber::try_from(number) {
        Ok(SeverityNumber::Trace) | Ok(SeverityNumber::Trace2) | Ok(SeverityNumber::Trace3) | Ok(SeverityNumber::Trace4) => Some("Trace".into()),
        Ok(SeverityNumber::Debug) | Ok(SeverityNumber::Debug2) | Ok(SeverityNumber::Debug3) | Ok(SeverityNumber::Debug4) => Some("Debug".into()),
        Ok(SeverityNumber::Info) | Ok(SeverityNumber::Info2) | Ok(SeverityNumber::Info3) | Ok(SeverityNumber::Info4) => Some("Information".into()),
        Ok(SeverityNumber::Warn) | Ok(SeverityNumber::Warn2) | Ok(SeverityNumber::Warn3) | Ok(SeverityNumber::Warn4) => Some("Warning".into()),
        Ok(SeverityNumber::Error) | Ok(SeverityNumber::Error2) | Ok(SeverityNumber::Error3) | Ok(SeverityNumber::Error4) => Some("Error".into()),
        Ok(SeverityNumber::Fatal) | Ok(SeverityNumber::Fatal2) | Ok(SeverityNumber::Fatal3) | Ok(SeverityNumber::Fatal4) => Some("Critical".into()),
        _ => None,
    }
}

fn span_kind_label(kind: i32) -> String {
    use crate::proto::opentelemetry::proto::trace::v1::span::SpanKind;
    match SpanKind::try_from(kind) {
        Ok(SpanKind::Internal) => "Internal",
        Ok(SpanKind::Server) => "Server",
        Ok(SpanKind::Client) => "Client",
        Ok(SpanKind::Producer) => "Producer",
        Ok(SpanKind::Consumer) => "Consumer",
        _ => "Unspecified",
    }
    .to_string()
}

fn status_label(status: Option<&crate::proto::opentelemetry::proto::trace::v1::Status>) -> Option<String> {
    use crate::proto::opentelemetry::proto::trace::v1::status::StatusCode;
    let status = status?;
    match StatusCode::try_from(status.code) {
        Ok(StatusCode::Ok) => Some("Ok".into()),
        Ok(StatusCode::Error) => Some("Error".into()),
        _ => Some("Unset".into()),
    }
}

// ---------------------------------------------------------------------------
// Ingestion (shared by gRPC and HTTP paths)
// ---------------------------------------------------------------------------

fn ingest_traces(shared: &OtlpShared, request: ExportTraceServiceRequest) {
    for resource_spans in &request.resource_spans {
        let res_name = resource_name(resource_spans.resource.as_ref());
        let targets = shared.target_sessions(res_name.as_deref());
        for session in &targets {
            let mut store = session.telemetry.lock().unwrap();
            for scope_spans in &resource_spans.scope_spans {
                for span in &scope_spans.spans {
                    let duration = span.end_time_unix_nano.saturating_sub(span.start_time_unix_nano);
                    store.push_span(SpanSummary {
                        trace_id: hex::encode(&span.trace_id),
                        span_id: hex::encode(&span.span_id),
                        parent_span_id: if span.parent_span_id.is_empty() {
                            None
                        } else {
                            Some(hex::encode(&span.parent_span_id))
                        },
                        name: span.name.clone(),
                        kind: span_kind_label(span.kind),
                        resource_name: res_name.clone(),
                        start_unix_nano: span.start_time_unix_nano.to_string(),
                        duration_nanos: duration.to_string(),
                        status_code: status_label(span.status.as_ref()),
                    });
                }
            }
        }
    }
    shared.maybe_emit();
}

fn ingest_logs(shared: &OtlpShared, request: ExportLogsServiceRequest) {
    for resource_logs in &request.resource_logs {
        let res_name = resource_name(resource_logs.resource.as_ref());
        let targets = shared.target_sessions(res_name.as_deref());
        for session in &targets {
            let mut store = session.telemetry.lock().unwrap();
            for scope_logs in &resource_logs.scope_logs {
                for record in &scope_logs.log_records {
                    let body = record
                        .body
                        .as_ref()
                        .map(any_value_to_string)
                        .unwrap_or_default();
                    store.push_log(LogRecordSummary {
                        time_unix_nano: record.time_unix_nano.to_string(),
                        severity: severity_label(record.severity_number),
                        severity_number: record.severity_number,
                        body,
                        resource_name: res_name.clone(),
                        trace_id: if record.trace_id.is_empty() {
                            None
                        } else {
                            Some(hex::encode(&record.trace_id))
                        },
                        span_id: if record.span_id.is_empty() {
                            None
                        } else {
                            Some(hex::encode(&record.span_id))
                        },
                    });
                }
            }
        }
    }
    shared.maybe_emit();
}

fn ingest_metrics(shared: &OtlpShared, request: ExportMetricsServiceRequest) {
    use crate::proto::opentelemetry::proto::metrics::v1::metric::Data;
    use crate::proto::opentelemetry::proto::metrics::v1::number_data_point::Value as PointValue;
    use crate::proto::opentelemetry::proto::metrics::v1::AggregationTemporality;

    {
        for resource_metrics in &request.resource_metrics {
            let res_name = resource_name(resource_metrics.resource.as_ref());
            let targets = shared.target_sessions(res_name.as_deref());
            for session in &targets {
                let mut store = session.telemetry.lock().unwrap();
                for scope_metrics in &resource_metrics.scope_metrics {
                    for metric in &scope_metrics.metrics {
                        let unit = if metric.unit.is_empty() {
                            None
                        } else {
                            Some(metric.unit.clone())
                        };
                        let res = res_name.as_deref();

                        match &metric.data {
                            Some(Data::Gauge(g)) => {
                                for p in &g.data_points {
                                    if let (Some(v), t) = (point_value(p), point_time_ms(p.time_unix_nano)) {
                                        store.record_number_point(&metric.name, unit.clone(), res, MetricKind::Gauge, t, v, false);
                                    }
                                }
                            }
                            Some(Data::Sum(s)) => {
                                // Monotonic sum is a counter (charted as a rate); a
                                // non-monotonic sum is an up/down counter (charted raw).
                                let kind = if s.is_monotonic { MetricKind::Counter } else { MetricKind::UpDownCounter };
                                let is_delta = s.aggregation_temporality == AggregationTemporality::Delta as i32;
                                for p in &s.data_points {
                                    if let (Some(v), t) = (point_value(p), point_time_ms(p.time_unix_nano)) {
                                        store.record_number_point(&metric.name, unit.clone(), res, kind, t, v, is_delta);
                                    }
                                }
                            }
                            Some(Data::Histogram(h)) => {
                                let is_delta = h.aggregation_temporality == AggregationTemporality::Delta as i32;
                                for p in &h.data_points {
                                    let t = point_time_ms(p.time_unix_nano);
                                    store.record_histogram_point(
                                        &metric.name,
                                        unit.clone(),
                                        res,
                                        t,
                                        &p.explicit_bounds,
                                        &p.bucket_counts,
                                        is_delta,
                                    );
                                }
                            }
                            _ => {}
                        }
                    }
                }
            }
        }
    }

    fn point_value(p: &crate::proto::opentelemetry::proto::metrics::v1::NumberDataPoint) -> Option<f64> {
        match &p.value {
            Some(PointValue::AsDouble(d)) => Some(*d),
            Some(PointValue::AsInt(i)) => Some(*i as f64),
            None => None,
        }
    }

    // OTLP timestamps are unix nanoseconds; charts work in unix milliseconds.
    fn point_time_ms(time_unix_nano: u64) -> i64 {
        (time_unix_nano / 1_000_000) as i64
    }

    shared.maybe_emit();
}

// ---------------------------------------------------------------------------
// gRPC services
// ---------------------------------------------------------------------------

fn grpc_auth_ok<T>(shared: &OtlpShared, request: &Request<T>) -> Result<(), Status> {
    let provided = request
        .metadata()
        .get("x-otlp-api-key")
        .map(|v| v.as_bytes().to_vec());
    if shared.auth.check(provided.as_deref()) {
        Ok(())
    } else {
        Err(Status::unauthenticated("invalid or missing x-otlp-api-key"))
    }
}

struct GrpcTraceService {
    shared: Arc<OtlpShared>,
}

#[tonic::async_trait]
impl TraceService for GrpcTraceService {
    async fn export(
        &self,
        request: Request<ExportTraceServiceRequest>,
    ) -> Result<Response<ExportTraceServiceResponse>, Status> {
        grpc_auth_ok(&self.shared, &request)?;
        ingest_traces(&self.shared, request.into_inner());
        Ok(Response::new(ExportTraceServiceResponse::default()))
    }
}

struct GrpcLogsService {
    shared: Arc<OtlpShared>,
}

#[tonic::async_trait]
impl LogsService for GrpcLogsService {
    async fn export(
        &self,
        request: Request<ExportLogsServiceRequest>,
    ) -> Result<Response<ExportLogsServiceResponse>, Status> {
        grpc_auth_ok(&self.shared, &request)?;
        ingest_logs(&self.shared, request.into_inner());
        Ok(Response::new(ExportLogsServiceResponse::default()))
    }
}

struct GrpcMetricsService {
    shared: Arc<OtlpShared>,
}

#[tonic::async_trait]
impl MetricsService for GrpcMetricsService {
    async fn export(
        &self,
        request: Request<ExportMetricsServiceRequest>,
    ) -> Result<Response<ExportMetricsServiceResponse>, Status> {
        grpc_auth_ok(&self.shared, &request)?;
        ingest_metrics(&self.shared, request.into_inner());
        Ok(Response::new(ExportMetricsServiceResponse::default()))
    }
}

// ---------------------------------------------------------------------------
// HTTP server (axum)
// ---------------------------------------------------------------------------

mod http {
    use super::*;
    use axum::body::Bytes;
    use axum::extract::State;
    use axum::http::{HeaderMap, StatusCode};
    use axum::response::IntoResponse;
    use axum::routing::post;
    use axum::Router;
    use prost::Message;

    fn http_auth_ok(shared: &OtlpShared, headers: &HeaderMap) -> bool {
        let provided = headers.get("x-otlp-api-key").map(|v| v.as_bytes());
        shared.auth.check(provided)
    }

    async fn traces(State(shared): State<Arc<OtlpShared>>, headers: HeaderMap, body: Bytes) -> impl IntoResponse {
        if !http_auth_ok(&shared, &headers) {
            return StatusCode::UNAUTHORIZED;
        }
        match ExportTraceServiceRequest::decode(body) {
            Ok(req) => {
                ingest_traces(&shared, req);
                StatusCode::OK
            }
            // We currently decode protobuf only; JSON-encoded OTLP bodies are
            // accepted but not parsed in the MVP.
            Err(_) => StatusCode::OK,
        }
    }

    async fn logs(State(shared): State<Arc<OtlpShared>>, headers: HeaderMap, body: Bytes) -> impl IntoResponse {
        if !http_auth_ok(&shared, &headers) {
            return StatusCode::UNAUTHORIZED;
        }
        match ExportLogsServiceRequest::decode(body) {
            Ok(req) => {
                ingest_logs(&shared, req);
                StatusCode::OK
            }
            Err(_) => StatusCode::OK,
        }
    }

    async fn metrics(State(shared): State<Arc<OtlpShared>>, headers: HeaderMap, body: Bytes) -> impl IntoResponse {
        if !http_auth_ok(&shared, &headers) {
            return StatusCode::UNAUTHORIZED;
        }
        match ExportMetricsServiceRequest::decode(body) {
            Ok(req) => {
                ingest_metrics(&shared, req);
                StatusCode::OK
            }
            Err(_) => StatusCode::OK,
        }
    }

    pub fn router(shared: Arc<OtlpShared>) -> Router {
        Router::new()
            .route("/v1/traces", post(traces))
            .route("/v1/logs", post(logs))
            .route("/v1/metrics", post(metrics))
            .with_state(shared)
    }
}

// ---------------------------------------------------------------------------
// Server lifecycle
// ---------------------------------------------------------------------------

fn parse_socket_addr(url: &str) -> Option<SocketAddr> {
    // Accept either a bare host:port or a full URL such as
    // "http://localhost:4317". Bind to the loopback interface for the resolved
    // port; the dashboard binds OTLP to localhost only as well.
    let trimmed = url
        .trim()
        .trim_start_matches("https://")
        .trim_start_matches("http://")
        .trim_end_matches('/');
    let port = trimmed.rsplit(':').next()?.parse::<u16>().ok()?;
    Some(SocketAddr::from(([127, 0, 0, 1], port)))
}

/// Starts the OTLP gRPC and HTTP servers in the background. Connection status is
/// reported to the UI via `deck://connection`.
pub fn start(app: AppHandle, config: &crate::config::OtlpConfig, store: Arc<OtlpShared>) {
    // gRPC server.
    if let Some(addr) = parse_socket_addr(&config.grpc_url) {
        let shared = store.clone();
        let app_handle = app.clone();
        let grpc_url = config.grpc_url.clone();
        tauri::async_runtime::spawn(async move {
            let trace = TraceServiceServer::new(GrpcTraceService { shared: shared.clone() });
            let logs = LogsServiceServer::new(GrpcLogsService { shared: shared.clone() });
            let metrics = MetricsServiceServer::new(GrpcMetricsService { shared: shared.clone() });

            let _ = app_handle.emit(
                "deck://connection",
                &ConnectionStatus::new("otlpGrpc", "connected", Some(grpc_url.clone())),
            );

            if let Err(err) = Server::builder()
                .add_service(trace)
                .add_service(logs)
                .add_service(metrics)
                .serve(addr)
                .await
            {
                let _ = app_handle.emit(
                    "deck://connection",
                    &ConnectionStatus::new("otlpGrpc", "error", Some(err.to_string())),
                );
            }
        });
    } else {
        let _ = app.emit(
            "deck://connection",
            &ConnectionStatus::new("otlpGrpc", "error", Some("invalid gRPC endpoint URL".into())),
        );
    }

    // HTTP server.
    if let Some(addr) = parse_socket_addr(&config.http_url) {
        let shared = store.clone();
        let app_handle = app.clone();
        let http_url = config.http_url.clone();
        tauri::async_runtime::spawn(async move {
            let router = http::router(shared);
            match tokio::net::TcpListener::bind(addr).await {
                Ok(listener) => {
                    let _ = app_handle.emit(
                        "deck://connection",
                        &ConnectionStatus::new("otlpHttp", "connected", Some(http_url.clone())),
                    );
                    if let Err(err) = axum::serve(listener, router).await {
                        let _ = app_handle.emit(
                            "deck://connection",
                            &ConnectionStatus::new("otlpHttp", "error", Some(err.to_string())),
                        );
                    }
                }
                Err(err) => {
                    let _ = app_handle.emit(
                        "deck://connection",
                        &ConnectionStatus::new("otlpHttp", "error", Some(err.to_string())),
                    );
                }
            }
        });
    } else {
        let _ = app.emit(
            "deck://connection",
            &ConnectionStatus::new("otlpHttp", "error", Some("invalid HTTP endpoint URL".into())),
        );
    }
}
