// Aspire Go validation AppHost - Aspire.Hosting.Azure.PostgreSQL
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

	pg, err := builder.AddAzurePostgresFlexibleServer("resource")
	if err != nil {
		log.Fatalf("AddAzurePostgresFlexibleServer: %v", err)
	}
	_, _ = pg.AddDatabase("resource", nil)

	pgAuth, err := builder.AddAzurePostgresFlexibleServer("resource")
	if err != nil {
		log.Fatalf("AddAzurePostgresFlexibleServer: %v", err)
	}
	pgAuth.WithPasswordAuthentication(nil, nil)

	pgContainer, err := builder.AddAzurePostgresFlexibleServer("resource")
	if err != nil {
		log.Fatalf("AddAzurePostgresFlexibleServer: %v", err)
	}
	pgContainer.RunAsContainer(nil)
	_, _ = pgContainer.AddDatabase("resource", nil)

	app, err := builder.Build()
	if err != nil {
		log.Fatalf("Build: %v", err)
	}
	if err := app.Run(nil); err != nil {
		log.Fatalf("Run: %v", err)
	}
}
