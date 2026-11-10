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

	// Keep Go modules current and send traces, metrics, and logs to the dashboard.
	api := builder.AddGoApp("api", "./api").
		WithModTidy().
		WithHttpEndpoint(aspire.HttpEndpointEnv("PORT")).
		WithHttpHealthCheck(aspire.HttpHealthCheckPath("/health")).
		WithExternalHttpEndpoints().
		WithOtlpExporter()

	// Reference the API for Vite proxying and wait until it is healthy.
	frontend := builder.AddViteApp("frontend", "./frontend", nil).
		WithReference(api).
		WaitFor(api)

	// Bundle the frontend into the API container for same-origin production serving.
	api.PublishWithContainerFiles(frontend, "./static")
	api.PublishAsDockerFile(func(container aspire.ContainerResource) {
		container.WithEnvironment("ASPIRE_BIND_ADDRESS", "0.0.0.0")
	})
	// {{#redis}}

	// Expose cache connection properties, including CACHE_URI, to the API.
	cache := builder.AddRedis("cache")
	api.WithReference(cache).WaitFor(cache)
	// {{/redis}}

	app, err := builder.Build()
	if err != nil {
		log.Fatal(aspire.FormatError(err))
	}
	if err := app.Run(); err != nil {
		log.Fatal(aspire.FormatError(err))
	}
}
