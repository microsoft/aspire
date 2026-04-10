// Aspire Go validation AppHost - Aspire.Hosting.Docker
// Mirrors the TypeScript/Python/Java fixture for API surface validation.
// Run `aspire restore --apphost apphost.go` to generate the SDK, then `go build ./...`.
package main

import (
	"log"

	"apphost/modules/aspire"
)

func main() {
	builder, err := aspire.CreateBuilder(nil)
	if err != nil {
		log.Fatalf("CreateBuilder: %v", err)
	}

	compose := builder.AddDockerComposeEnvironment("resource")
	compose.WithProperties(nil)
	compose.WithDashboard(nil)
	compose.WithDashboard(nil)
	compose.ConfigureDashboard(nil)
	_, _ = compose.DefaultNetworkName()
	_, _ = compose.DashboardEnabled()
	_, _ = compose.Name()
	if err = compose.Err(); err != nil {
		log.Fatalf("compose: %v", err)
	}

	api := builder.AddContainer("resource", "image")
	_, _ = api.PublishAsDockerComposeService(nil)
	if err = api.Err(); err != nil {
		log.Fatalf("api: %v", err)
	}

	app, err := builder.Build()
	if err != nil {
		log.Fatalf("Build: %v", err)
	}
	if err := app.Run(nil); err != nil {
		log.Fatalf("Run: %v", err)
	}
}
