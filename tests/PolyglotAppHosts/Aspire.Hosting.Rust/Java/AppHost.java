import aspire.*;

void main() throws Exception {
        var builder = DistributedApplication.CreateBuilder();

        // Basic Rust app — cargo run
        var api = builder.addRustApp("api", "../rust-api");

        // Workspace member built in release mode with an explicit binary target and features
        var worker = builder.addRustApp("worker", "../rust-workspace")
            .withCargoPackage("worker")
            .withCargoBinTarget("worker-cli")
            .withCargoFeatures(new String[] { "telemetry", "postgres" })
            .withCargoReleaseBuild(true)
            .withCargoLocked(true);

        // Custom cargo profile and target triple, driven from a manifest outside the app directory
        var tool = builder.addRustApp("tool", "../rust-tools")
            .withCargoManifestPath("../rust-tools/Cargo.toml")
            .withCargoProfile("dist")
            .withCargoTarget("x86_64-unknown-linux-musl")
            .withCargoArgs(new String[] { "--timings" });

        // Cargo example target
        var sample = builder.addRustApp("sample", "../rust-samples")
            .withCargoExample("hello");

        builder.build().run();
    }
