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

	// Basic Go app — go run .
	api := builder.AddGoApp("api", "../go-api")
	if err = api.Err(); err != nil {
		log.Fatal(aspire.FormatError(err))
	}

	// Go app with build tags and linker flags via AddGoAppOptions
	worker := builder.AddGoApp("worker", "../go-worker", &aspire.AddGoAppOptions{
		BuildTags: []string{"netgo", "osusergo"},
		LdFlags:   aspire.StringPtr("-s -w -X main.version=1.0.0"),
	})
	if err = worker.Err(); err != nil {
		log.Fatal(aspire.FormatError(err))
	}

	// Go app with pre-start lifecycle helpers and all build options
	managed := builder.AddGoApp("managed", "../go-managed", &aspire.AddGoAppOptions{
		BuildTags:    []string{"integration"},
		LdFlags:      aspire.StringPtr("-s -w"),
		GcFlags:      aspire.StringPtr("all=-N -l"),
		RaceDetector: aspire.BoolPtr(true),
	}).
		WithModTidy().
		WithModVendor().
		WithModDownload().
		WithVetTool().
		WithAppArgs([]any{"--config", "prod.yaml"})
	if err = managed.Err(); err != nil {
		log.Fatal(aspire.FormatError(err))
	}

	// Go app with headless Delve server for remote debugging (GoLand / VS Code attach)
	debugService := builder.AddGoApp("debug-service", "../go-debug-service").
		WithDelveServer(&aspire.WithDelveServerOptions{Port: aspire.Float64Ptr(2345)})
	if err = debugService.Err(); err != nil {
		log.Fatal(aspire.FormatError(err))
	}

	app, err := builder.Build()
	if err != nil {
		log.Fatal(aspire.FormatError(err))
	}
	if err := app.Run(nil); err != nil {
		log.Fatal(aspire.FormatError(err))
	}
}
