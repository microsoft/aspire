package main

import (
	"log"

	"apphost/modules/aspire"
)

func main() {
	builder, err := aspire.CreateBuilder()
	if err != nil {
		log.Fatal(aspire.FormatError(err))
	}

	// Basic Rust app — cargo run
	api := builder.AddRustApp("api", "../rust-api")
	if err = api.Err(); err != nil {
		log.Fatal(aspire.FormatError(err))
	}

	// Workspace member built in release mode with an explicit binary target and features
	worker := builder.AddRustApp("worker", "../rust-workspace").
		WithCargoPackage("worker").
		WithCargoBinTarget("worker-cli").
		WithCargoFeatures([]string{"telemetry", "postgres"}).
		WithCargoReleaseBuild(&aspire.WithCargoReleaseBuildOptions{ReleaseBuild: aspire.BoolPtr(true)}).
		WithCargoLocked(&aspire.WithCargoLockedOptions{Locked: aspire.BoolPtr(true)})
	if err = worker.Err(); err != nil {
		log.Fatal(aspire.FormatError(err))
	}

	// Custom cargo profile and target triple, driven from a manifest outside the app directory
	tool := builder.AddRustApp("tool", "../rust-tools").
		WithCargoManifestPath("../rust-tools/Cargo.toml").
		WithCargoProfile("dist").
		WithCargoTarget("x86_64-unknown-linux-musl").
		WithCargoArgs([]string{"--timings"})
	if err = tool.Err(); err != nil {
		log.Fatal(aspire.FormatError(err))
	}

	// Cargo example target
	sample := builder.AddRustApp("sample", "../rust-samples").
		WithCargoExample("hello")
	if err = sample.Err(); err != nil {
		log.Fatal(aspire.FormatError(err))
	}

	app, err := builder.Build()
	if err != nil {
		log.Fatal(aspire.FormatError(err))
	}
	if err := app.Run(); err != nil {
		log.Fatal(aspire.FormatError(err))
	}
}
