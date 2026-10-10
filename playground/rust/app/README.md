# Rust sample application

This application serves `/`, `/health`, `/ping`, and `/error`, and exports traces,
metrics, and logs over OTLP/HTTP protobuf. Run it through the AppHost in the parent
directory, or use `cargo run --locked` here with an OTLP collector configured via
the standard `OTEL_EXPORTER_OTLP_*` environment variables.

## TLS provider

The HTTP exporters use Reqwest with `native-tls`: SChannel on Windows, OpenSSL on
Linux, and Secure Transport on macOS. Reqwest's default features are disabled
because version 0.13 defaults to rustls. Do not enable `reqwest-rustls`, gRPC TLS
features, or Reqwest's defaults alongside native TLS: Cargo features are additive.
See the [native-tls documentation](https://docs.rs/native-tls/latest/native_tls/).

HTTPS collectors must present a valid certificate for their hostname, trusted by
the platform TLS library. HTTP collectors remain supported. Certificate
verification is not disabled. Both the Rust and C# AppHosts select the dashboard's
HTTP/protobuf OTLP endpoint rather than the default gRPC endpoint.

When running directly, set `OTEL_EXPORTER_OTLP_ENDPOINT` to the collector's HTTP
endpoint (typically port 4318) and `OTEL_EXPORTER_OTLP_PROTOCOL=http/protobuf`.
The exporter appends `/v1/traces`, `/v1/metrics`, and `/v1/logs` to the general
endpoint; signal-specific endpoint variables must include the complete path.
Standard OTLP headers and per-signal settings remain supported.

Reqwest and Hyper diagnostics are excluded from exported logs to prevent export
feedback loops. They remain available in console output through `RUST_LOG`.

Linux builds require OpenSSL development libraries and `pkg-config`; runtime
environments need compatible OpenSSL libraries and trusted CA certificates.
OpenSSL honors `SSL_CERT_FILE` and `SSL_CERT_DIR` for custom trust, including the
certificate settings supplied by Aspire. Windows and macOS use their platform
TLS libraries and certificate stores, not OpenSSL. See the
[rust-openssl build requirements](https://docs.rs/openssl/latest/openssl/#building).

## Checking the dependency graph

From this directory:

```shell
cargo check --locked --all-targets
cargo build --locked
cargo test --locked
cargo tree --locked --target all --all-features -e features -i native-tls
```

The native-tls tree should lead through Reqwest to the exporters. Neither the
resolved graph nor `Cargo.lock` should contain `ring`, `rustls`, or `aws-lc-rs`.
Regenerate the lockfile with Cargo after changing dependencies; do not remove
individual entries by hand.
