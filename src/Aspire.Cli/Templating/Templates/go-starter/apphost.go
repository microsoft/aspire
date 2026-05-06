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

	// A Redis cache the API will use for read-through caching.
	cache := builder.AddRedis("cache")

	// Bootstrap step: resolve api module dependencies (creates go.sum on first
	// run). Modeled as its own resource so the dashboard surfaces its logs and
	// the api waits for it to exit cleanly before launching.
	apiTidy := builder.AddExecutable("api-tidy", "go", "./api", []string{"mod", "tidy"})

	// The Go HTTP API. Aspire injects:
	//   - "ConnectionStrings__cache" so the API can dial Redis.
	//   - "OTEL_EXPORTER_OTLP_*" env vars (via WithOtlpExporter) so traces,
	//     metrics, and logs flow to the Aspire dashboard.
	builder.AddExecutable("api", "go", "./api", []string{"run", "."}).
		WaitForCompletion(apiTidy).
		WithReference(cache).
		WaitFor(cache).
		WithHttpEndpoint(&aspire.WithHttpEndpointOptions{Env: aspire.StringPtr("PORT")}).
		WithExternalHttpEndpoints().
		WithOtlpExporter()

	if err := builder.Err(); err != nil {
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
