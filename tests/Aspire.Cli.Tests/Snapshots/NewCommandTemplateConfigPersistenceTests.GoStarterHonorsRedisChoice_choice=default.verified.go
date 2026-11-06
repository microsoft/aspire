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

	// The Go HTTP API. WithModTidy runs "go mod tidy" before launching so that
	// go.sum is always up to date. Aspire injects:
	//   - "OTEL_EXPORTER_OTLP_*" env vars (via WithOtlpExporter) so traces,
	//     metrics, and logs flow to the Aspire dashboard.
	api := builder.AddGoApp("api", "./api").
		WithModTidy().
		WithHttpEndpoint(&aspire.WithHttpEndpointOptions{Env: aspire.StringPtr("PORT")}).
		WithHttpHealthCheck(&aspire.WithHttpHealthCheckOptions{Path: aspire.StringPtr("/health")}).
		WithExternalHttpEndpoints().
		WithOtlpExporter()

	// Aspire injects API_HTTP for the frontend's Vite proxy and delays the frontend
	// until the API is ready to serve /api/weatherforecast.
	frontend := builder.AddViteApp("frontend", "./frontend", nil).
		WithReference(api).
		WaitFor(api)

	// In publish and deploy mode, copy the frontend build output into the Go API
	// container. The API serves these files from ./static on the same origin as /api.
	api.PublishWithContainerFiles(frontend, "./static")
	resources := []aspire.Resource{api, frontend}

	// WithReference injects ConnectionStrings__cache for read-through caching.
	cache := builder.AddRedis("cache")
	api.WithReference(cache).WaitFor(cache)
	resources = append(resources, cache)

	// Each call records its failure on the handle that it returns, so check every resource.
	for _, resource := range resources {
		if err := resource.Err(); err != nil {
			log.Fatal(aspire.FormatError(err))
		}
	}

	app, err := builder.Build()
	if err != nil {
		log.Fatal(aspire.FormatError(err))
	}
	if err := app.Run(); err != nil {
		log.Fatal(aspire.FormatError(err))
	}
}
