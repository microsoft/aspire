//! OTLP ingestion. Aspire Deck hosts the same OpenTelemetry endpoints the Blazor
//! dashboard does, so telemetry exporters configured against the dashboard work
//! unchanged: a gRPC endpoint implementing the OTLP `Export` RPCs for traces,
//! metrics and logs, plus an HTTP endpoint exposing `/v1/{traces,metrics,logs}`.
//!
//! Ingested signals are summarized into an in-memory store (recent records are
//! capped) and pushed to the UI via the `deck://telemetry` event.

use std::collections::VecDeque;
use std::net::SocketAddr;
use std::sync::{Arc, Mutex};
use std::time::{Duration, Instant};

use indexmap::IndexMap;
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

// ---------------------------------------------------------------------------
// Store
// ---------------------------------------------------------------------------

pub struct TelemetryStore {
    log_count: u64,
    span_count: u64,
    metric_count: u64,
    recent_logs: VecDeque<LogRecordSummary>,
    recent_spans: VecDeque<SpanSummary>,
    metrics: IndexMap<String, MetricSummary>,
    last_emit: Option<Instant>,
}

impl TelemetryStore {
    pub fn new() -> Self {
        TelemetryStore {
            log_count: 0,
            span_count: 0,
            metric_count: 0,
            recent_logs: VecDeque::with_capacity(RECENT_CAP),
            recent_spans: VecDeque::with_capacity(RECENT_CAP),
            metrics: IndexMap::new(),
            last_emit: None,
        }
    }

    fn push_log(&mut self, log: LogRecordSummary) {
        self.log_count += 1;
        if self.recent_logs.len() >= RECENT_CAP {
            self.recent_logs.pop_back();
        }
        self.recent_logs.push_front(log);
    }

    fn push_span(&mut self, span: SpanSummary) {
        self.span_count += 1;
        if self.recent_spans.len() >= RECENT_CAP {
            self.recent_spans.pop_back();
        }
        self.recent_spans.push_front(span);
    }

    fn record_metric(&mut self, name: String, unit: Option<String>, resource: Option<String>, value: Option<f64>) {
        self.metric_count += 1;
        let entry = self.metrics.entry(name.clone()).or_insert_with(|| MetricSummary {
            name,
            unit: unit.clone(),
            resource_name: resource.clone(),
            last_value: None,
            point_count: 0,
        });
        entry.point_count += 1;
        if value.is_some() {
            entry.last_value = value;
        }
        if entry.unit.is_none() {
            entry.unit = unit;
        }
        if entry.resource_name.is_none() {
            entry.resource_name = resource;
        }
    }

    pub fn summary(&self) -> TelemetrySummary {
        TelemetrySummary {
            log_count: self.log_count,
            span_count: self.span_count,
            metric_count: self.metric_count,
            recent_logs: self.recent_logs.iter().cloned().collect(),
            recent_spans: self.recent_spans.iter().cloned().collect(),
            metrics: self.metrics.values().cloned().collect(),
        }
    }
}

impl Default for TelemetryStore {
    fn default() -> Self {
        Self::new()
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
    pub store: Mutex<TelemetryStore>,
    pub app: AppHandle,
    pub auth: OtlpAuth,
}

impl OtlpShared {
    /// Emits a debounced `deck://telemetry` event. We avoid emitting on every
    /// single export under load by coalescing to at most one event per
    /// `EMIT_DEBOUNCE` window.
    fn maybe_emit(&self) {
        let summary = {
            let mut store = self.store.lock().unwrap();
            let now = Instant::now();
            let should_emit = match store.last_emit {
                None => true,
                Some(last) => now.duration_since(last) >= EMIT_DEBOUNCE,
            };
            if !should_emit {
                return;
            }
            store.last_emit = Some(now);
            store.summary()
        };
        let _ = self.app.emit("deck://telemetry", &summary);
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
    {
        let mut store = shared.store.lock().unwrap();
        for resource_spans in &request.resource_spans {
            let res_name = resource_name(resource_spans.resource.as_ref());
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
    {
        let mut store = shared.store.lock().unwrap();
        for resource_logs in &request.resource_logs {
            let res_name = resource_name(resource_logs.resource.as_ref());
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
    {
        let mut store = shared.store.lock().unwrap();
        for resource_metrics in &request.resource_metrics {
            let res_name = resource_name(resource_metrics.resource.as_ref());
            for scope_metrics in &resource_metrics.scope_metrics {
                for metric in &scope_metrics.metrics {
                    let unit = if metric.unit.is_empty() {
                        None
                    } else {
                        Some(metric.unit.clone())
                    };
                    // Extract the most recent numeric value from gauge/sum points.
                    let last_value = match &metric.data {
                        Some(Data::Gauge(g)) => g.data_points.last().and_then(point_value),
                        Some(Data::Sum(s)) => s.data_points.last().and_then(point_value),
                        _ => None,
                    };
                    store.record_metric(metric.name.clone(), unit, res_name.clone(), last_value);
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
