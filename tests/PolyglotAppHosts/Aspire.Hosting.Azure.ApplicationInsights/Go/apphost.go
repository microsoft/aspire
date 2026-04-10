// Aspire Go validation AppHost - Aspire.Hosting.Azure.ApplicationInsights
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

	_, err = builder.AddAzureApplicationInsights("resource")
	if err != nil {
		log.Fatalf("AddAzureApplicationInsights: %v", err)
	}

	_, err = builder.AddAzureLogAnalyticsWorkspace("resource")
	if err != nil {
		log.Fatalf("AddAzureLogAnalyticsWorkspace: %v", err)
	}

	app, err := builder.Build()
	if err != nil {
		log.Fatalf("Build: %v", err)
	}
	if err := app.Run(nil); err != nil {
		log.Fatalf("Run: %v", err)
	}
}
