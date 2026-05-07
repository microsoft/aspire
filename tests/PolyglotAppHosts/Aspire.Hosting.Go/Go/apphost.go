package main

import (
	"log"

	"apphost/modules/aspire"
)

func main() {
	builder, err := aspire.CreateBuilder()
	if err != nil {
		log.Fatalf(aspire.FormatError(err))
	}

	// Basic Go app — go run .
	api := builder.AddGoApp("api", "../go-api")
	if err = api.Err(); err != nil {
		log.Fatalf(aspire.FormatError(err))
	}

	// Go app with build tags and linker flags
	worker := builder.AddGoApp("worker", "../go-worker")
	worker.WithBuildTags([]string{"netgo", "osusergo"}).
		WithLdFlags("-s -w -X main.version=1.0.0")
	if err = worker.Err(); err != nil {
		log.Fatalf(aspire.FormatError(err))
	}

	// Go app with pre-start lifecycle helpers and debug-friendly compiler flags
	managed := builder.AddGoApp("managed", "../go-managed").
		WithTidy().
		WithVendor().
		WithVet().
		WithRaceDetector().
		WithGcFlags("all=-N -l").
		WithAppArgs([]string{"--config", "prod.yaml"})
	if err = managed.Err(); err != nil {
		log.Fatalf(aspire.FormatError(err))
	}

	// Go app with headless Delve server for remote debugging (GoLand / VS Code attach)
	debugger := builder.AddGoApp("debugger", "../go-debugger").
		WithDelveServer(&aspire.WithDelveServerOptions{Port: aspire.Float64Ptr(2345)})
	if err = debugger.Err(); err != nil {
		log.Fatalf(aspire.FormatError(err))
	}

	app, err := builder.Build()
	if err != nil {
		log.Fatalf(aspire.FormatError(err))
	}
	if err := app.Run(nil); err != nil {
		log.Fatalf(aspire.FormatError(err))
	}
}
