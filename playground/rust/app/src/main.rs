mod telemetry;

use axum::extract::{MatchedPath, Request};
use axum::http::StatusCode;
use axum::middleware::{self, Next};
use axum::response::Response;
use axum::{routing::get, Router};
use opentelemetry::trace::Status;
use std::net::SocketAddr;
use std::time::Instant;
use telemetry::{init_telemetry, record_metrics};
use tracing::Instrument;
use tracing_opentelemetry::OpenTelemetrySpanExt;

#[tokio::main]
async fn main() -> Result<(), Box<dyn std::error::Error + Send + Sync>> {
    let telemetry = init_telemetry()?;
    let port = std::env::var("PORT")
        .ok()
        .and_then(|value| value.parse::<u16>().ok())
        .unwrap_or(8080);

    // Bind to all interfaces rather than loopback so the app is reachable from other machines and
    // from containers on the same host, not only from the machine that started it.
    let address = SocketAddr::from(([0, 0, 0, 0], port));
    tracing::info!(%address, "starting rust sample");

    // `layer` (rather than `route_layer`) wraps every request, including ones that
    // don't match any route, so 404s get traced and measured too - `route_layer`
    // would skip the middleware entirely for unmatched paths. `MatchedPath` is
    // simply absent on those requests; `instrument_request` falls back to the raw
    // request path in that case.
    let app = Router::new()
        .route("/", get(|| async { "Hello World from Rust" }))
        .route("/health", get(|| async { "healthy" }))
        .route("/ping", get(|| async { "healthy" }))
        .route("/error", get(|| async { StatusCode::INTERNAL_SERVER_ERROR }))
        .layer(middleware::from_fn(instrument_request));

    let listener = tokio::net::TcpListener::bind(address).await?;
    let server_result = axum::serve(listener, app).await;
    telemetry.shutdown();
    server_result?;
    Ok(())
}

async fn instrument_request(req: Request, next: Next) -> Response {
    let method = req.method().clone();
    let route = req
        .extensions()
        .get::<MatchedPath>()
        .map(|matched_path| matched_path.as_str().to_owned())
        .unwrap_or_else(|| req.uri().path().to_owned());

    // `otel.name`/`otel.kind` are special fields recognized by `tracing_opentelemetry`'s
    // layer to control the exported span's name/kind, mirroring what the previous
    // manual `span_builder(...).with_kind(SpanKind::Server)` call did.
    let span_name = format!("{method} {route}");
    let span = tracing::info_span!(
        "http.request",
        otel.kind = "server",
        otel.name = span_name.as_str(),
    );

    // Instrumenting the whole future (rather than just holding an `.enter()` guard)
    // keeps this span "current" every time the future is polled, including across
    // the `.await` below - that's what lets logs emitted anywhere during request
    // handling (here or in a route handler) carry this span's trace_id/span_id.
    async move {
        tracing::info!(%method, %route, "handling request");

        let start = Instant::now();
        let response = next.run(req).await;
        let elapsed_secs = start.elapsed().as_secs_f64();

        let status = response.status();
        record_metrics(&route, status, elapsed_secs);

        tracing::Span::current().set_status(if status.is_server_error() {
            Status::error(status.to_string())
        } else {
            Status::Ok
        });

        response
    }
    .instrument(span)
    .await
}
