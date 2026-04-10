// Aspire Go validation AppHost - Aspire.Hosting.Azure.OperationalInsights
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

	logAnalytics, err := builder.AddAzureLogAnalyticsWorkspace("resource")
	if err != nil {
		log.Fatalf("AddAzureLogAnalyticsWorkspace: %v", err)
	}
	_, _ = logAnalytics.WithUrl("http://localhost")

	app, err := builder.Build()
	if err != nil {
		log.Fatalf("Build: %v", err)
	}
	if err := app.Run(nil); err != nil {
		log.Fatalf("Run: %v", err)
	}
}
