# Rust sample application

This application serves `/`, `/health`, `/ping`, and `/error`, and exports traces,
metrics, and logs over OTLP/gRPC. Run it through the AppHost in the parent
directory, or use `cargo run --locked` here with an OTLP collector configured via
the standard `OTEL_EXPORTER_OTLP_*` environment variables.

## TLS provider

Both `opentelemetry-otlp` and `tonic` select `tls-aws-lc`, so rustls uses AWS-LC
instead of ring. Keep those feature selections consistent: Cargo features are
additive, and enabling `tls-ring` on either dependency would bring ring back.
There is only one enabled built-in provider, allowing rustls to select it without
a process-global provider override. This is the ordinary, non-FIPS AWS-LC backend;
it does not enable or claim FIPS mode.

The exporters still use `ClientTlsConfig::new().with_native_roots()`. HTTPS
collectors must present a valid certificate for their hostname, trusted by the
native certificate store. HTTP collectors remain supported. This change does not
disable certificate verification or change telemetry signals, endpoints, or
request handling.

Building AWS-LC requires native build tools. See the upstream requirements for
[Linux](https://aws.github.io/aws-lc-rs/requirements/linux.html),
[macOS](https://aws.github.io/aws-lc-rs/requirements/apple.html), and
[Windows](https://aws.github.io/aws-lc-rs/requirements/windows.html).
The selected features enable upstream prebuilt NASM objects for supported Windows
x86-64 targets when NASM is unavailable; Windows still needs a C/C++ toolchain.
Other targets can have additional requirements.

## Checking the dependency graph

From this directory:

```shell
cargo check --locked --all-targets
cargo build --locked
cargo test --locked
cargo tree --locked --target all --all-features -e features -i aws-lc-rs
cargo tree --locked --target all --all-features -e features -i ring
```

The AWS-LC tree should lead through rustls and tonic to the exporters. The ring
tree should have nothing to print. However, **ring still appears in `Cargo.lock`**
because rustls-webpki 0.103 uses the weak optional feature reference `ring?/alloc`.
This is tracked in [rust-lang/cargo#10801](https://github.com/rust-lang/cargo/issues/10801)
and [rustls/rustls#3093](https://github.com/rustls/rustls/issues/3093).
It is not enabled or compiled by this sample, but lockfile-based scanners can
still report it. Do not remove the entry by hand: Cargo regenerates it.
Removing that entry requires a supported upstream dependency chain or Cargo fix;
switching the active TLS provider alone does not resolve a lockfile-based finding.
