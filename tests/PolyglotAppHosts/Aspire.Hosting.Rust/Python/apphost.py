from aspire_app import create_builder


with create_builder() as builder:
    # Basic Rust app - cargo run
    builder.add_rust_app("api", "../rust-api")

    # Workspace member built in release mode with an explicit binary target and features
    worker = builder.add_rust_app("worker", "../rust-workspace")
    worker.with_cargo_package("worker")
    worker.with_cargo_bin_target("worker-cli")
    worker.with_cargo_features(["telemetry", "postgres"])
    worker.with_cargo_release_build(release_build=True)
    worker.with_cargo_locked(locked=True)

    # Custom cargo profile and target triple, driven from a manifest outside the app directory
    tool = builder.add_rust_app("tool", "../rust-tools")
    tool.with_cargo_manifest_path("../rust-tools/Cargo.toml")
    tool.with_cargo_profile("dist")
    tool.with_cargo_target("x86_64-unknown-linux-musl")
    tool.with_cargo_args(["--timings"])

    # Cargo example target
    sample = builder.add_rust_app("sample", "../rust-samples")
    sample.with_cargo_example("hello")

    builder.run()
