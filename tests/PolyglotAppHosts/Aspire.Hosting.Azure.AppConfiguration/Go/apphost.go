// Aspire Go validation AppHost - Aspire.Hosting.Azure.AppConfiguration
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

	appConfig, err := builder.AddAzureAppConfiguration("resource")
	if err != nil {
		log.Fatalf("AddAzureAppConfiguration: %v", err)
	}
	_, _ = appConfig.WithAppConfigurationRoleAssignments(appConfig, nil)
	if err := appConfig.RunAsEmulator(nil).Err(); err != nil {
		log.Fatalf("RunAsEmulator: %v", err)
	}

	app, err := builder.Build()
	if err != nil {
		log.Fatalf("Build: %v", err)
	}
	if err := app.Run(nil); err != nil {
		log.Fatalf("Run: %v", err)
	}
}
