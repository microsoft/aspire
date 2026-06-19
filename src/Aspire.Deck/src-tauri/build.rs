use std::path::PathBuf;

// Build script responsibilities:
//  1. Run tauri's build step (reads tauri.conf.json, wires up the app context).
//  2. Compile the gRPC contracts the Deck speaks. We compile the protobufs with
//     `protox` (a pure-Rust protobuf compiler) so a system `protoc` binary is not
//     required, then hand the resulting FileDescriptorSet to tonic-build.
fn main() {
    tauri_build::build();

    let protos = [
        // Resource service contract (AppHost -> Deck), identical to the one the
        // Blazor dashboard consumes.
        "proto/aspire/dashboard_service.proto",
        // OTLP ingestion contracts (telemetry -> Deck).
        "proto/opentelemetry/proto/collector/trace/v1/trace_service.proto",
        "proto/opentelemetry/proto/collector/metrics/v1/metrics_service.proto",
        "proto/opentelemetry/proto/collector/logs/v1/logs_service.proto",
    ];
    let includes = ["proto"];

    let file_descriptors = protox::compile(protos, includes).expect("failed to compile protobufs");

    let out_dir = PathBuf::from(std::env::var("OUT_DIR").expect("OUT_DIR not set"));

    tonic_build::configure()
        .build_client(true)
        .build_server(true)
        .out_dir(&out_dir)
        .compile_fds(file_descriptors)
        .expect("failed to generate gRPC bindings");

    println!("cargo:rerun-if-changed=proto");
}
