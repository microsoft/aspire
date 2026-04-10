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

	appConfig := builder.AddAzureAppConfiguration("resource")
	_, _ = appConfig.WithAppConfigurationRoleAssignments(appConfig, nil)
	appConfig.RunAsEmulator(nil)
	if err = appConfig.Err(); err != nil {
		log.Fatalf("appConfig: %v", err)
	}

	app, err := builder.Build()
	if err != nil {
		log.Fatalf("Build: %v", err)
	}
	if err := app.Run(nil); err != nil {
		log.Fatalf("Run: %v", err)
	}
}
