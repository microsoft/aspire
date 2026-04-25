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

	// The Go HTTP API. Aspire injects:
	//   - "ConnectionStrings__cache" so the API can dial Redis.
	//   - "OTEL_EXPORTER_OTLP_*" env vars (via WithOtlpExporter) so traces,
	//     metrics, and logs flow to the Aspire dashboard.
	builder.AddExecutable("api", "go", "./api", []string{"run", "."}).
		WithReference(cache).
		WaitFor(cache).
		WithHttpEndpoint().
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
