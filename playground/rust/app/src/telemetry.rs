use axum::http::StatusCode;
use opentelemetry::global;
use opentelemetry::trace::TracerProvider as _;
use opentelemetry::KeyValue;
use opentelemetry_appender_tracing::layer::OpenTelemetryTracingBridge;
use opentelemetry_otlp::WithTonicConfig;
use opentelemetry_sdk::logs::SdkLoggerProvider;
use opentelemetry_sdk::metrics::SdkMeterProvider;
use opentelemetry_sdk::trace::SdkTracerProvider;
use std::sync::OnceLock;
use tonic::transport::ClientTlsConfig;
use tracing_subscriber::{layer::SubscriberExt, util::SubscriberInitExt, EnvFilter};

static REQUEST_COUNTER: OnceLock<opentelemetry::metrics::Counter<u64>> = OnceLock::new();

fn get_request_counter() -> &'static opentelemetry::metrics::Counter<u64> {
    REQUEST_COUNTER.get_or_init(|| {
        global::meter("rust-apphost-playground")
            .u64_counter("http.server.request.count")
            .with_description("Total number of HTTP requests.")
            .build()
    })
}

static REQUEST_DURATION: OnceLock<opentelemetry::metrics::Histogram<f64>> = OnceLock::new();

fn get_request_duration_histogram() -> &'static opentelemetry::metrics::Histogram<f64> {
    REQUEST_DURATION.get_or_init(|| {
        global::meter("rust-apphost-playground")
            .f64_histogram("http.server.request.duration")
            .with_description("Duration of HTTP requests.")
            .with_unit("s")
            .build()
    })
}

/// Records the request counter and duration histogram for a completed request.
pub fn record_metrics(route: &str, status: StatusCode, elapsed_secs: f64) {
    let attributes = [
        KeyValue::new("http.route", route.to_owned()),
        KeyValue::new("http.response.status_code", status.as_u16() as i64),
    ];

    get_request_counter().add(1, &attributes);
    get_request_duration_histogram().record(elapsed_secs, &attributes);
}

pub fn init_telemetry() -> Result<OtelTelemetry, Box<dyn std::error::Error + Send + Sync>> {
    let tls = ClientTlsConfig::new().with_native_roots();

    let trace_exporter = opentelemetry_otlp::SpanExporter::builder()
        .with_tonic()
        .with_tls_config(tls.clone())
        .build()?;
    let tracer_provider = SdkTracerProvider::builder()
        .with_batch_exporter(trace_exporter)
        .build();
    global::set_tracer_provider(tracer_provider.clone());

    let metric_exporter = opentelemetry_otlp::MetricExporter::builder()
        .with_tonic()
        .with_tls_config(tls.clone())
        .build()?;
    let meter_provider = SdkMeterProvider::builder()
        .with_periodic_exporter(metric_exporter)
        .build();
    global::set_meter_provider(meter_provider.clone());

    let log_exporter = opentelemetry_otlp::LogExporter::builder()
        .with_tonic()
        .with_tls_config(tls)
        .build()?;
    let logger_provider = SdkLoggerProvider::builder()
        .with_batch_exporter(log_exporter)
        .build();

    // Get a concrete `SdkTracer` (rather than the type-erased `global::tracer(..)`
    // `BoxedTracer`, which doesn't implement `PreSampledTracer`) to hand to the
    // `tracing_opentelemetry` layer below. That layer is what makes `tracing`
    // spans/events show up correlated (shared trace_id/span_id) with the OTel
    // spans exported for this request - without it, spans created via the raw
    // `opentelemetry` API and logs created via `tracing` are two disconnected
    // systems with no shared context.
    let tracer = tracer_provider.tracer("rust-apphost-playground");

    let env_filter = EnvFilter::try_from_default_env().unwrap_or_else(|_| EnvFilter::new("info"));

    tracing_subscriber::registry()
        .with(env_filter)
        .with(tracing_subscriber::fmt::layer())
        .with(tracing_opentelemetry::layer().with_tracer(tracer))
        .with(OpenTelemetryTracingBridge::new(&logger_provider))
        .try_init()?;

    Ok(OtelTelemetry {
        tracer_provider,
        meter_provider,
        logger_provider,
    })
}

pub struct OtelTelemetry {
    tracer_provider: SdkTracerProvider,
    meter_provider: SdkMeterProvider,
    logger_provider: SdkLoggerProvider,
}

impl OtelTelemetry {
    pub fn shutdown(self) {
        if let Err(error) = self.tracer_provider.shutdown() {
            eprintln!("failed to shut down tracer provider: {error}");
        }
        if let Err(error) = self.meter_provider.shutdown() {
            eprintln!("failed to shut down meter provider: {error}");
        }
        if let Err(error) = self.logger_provider.shutdown() {
            eprintln!("failed to shut down logger provider: {error}");
        }
    }
}
