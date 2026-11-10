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
	app := builder.AddGoApp("app", "./api").
		WithModTidy().
		WithHttpEndpoint(aspire.HttpEndpointEnv("PORT")).
		WithHttpHealthCheck(aspire.HttpHealthCheckPath("/health")).
		WithExternalHttpEndpoints().
		WithOtlpExporter()

	// Reference the app for Vite proxying and wait until it is healthy.
	frontend := builder.AddViteApp("frontend", "./frontend", nil).
		WithReference(app).
		WaitFor(app)

	// Bundle the frontend into the app container for same-origin production serving.
	app.PublishWithContainerFiles(frontend, "./static")

	application, err := builder.Build()
	if err != nil {
		log.Fatal(aspire.FormatError(err))
	}
	if err := application.Run(); err != nil {
		log.Fatal(aspire.FormatError(err))
	}
}
