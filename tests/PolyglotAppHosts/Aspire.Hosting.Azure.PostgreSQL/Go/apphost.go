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

	pg := builder.AddAzurePostgresFlexibleServer("resource")
	_ = pg.AddDatabase("resource", nil)

	pgAuth := builder.AddAzurePostgresFlexibleServer("resource")
	pgAuth.WithPasswordAuthentication(nil, nil)

	pgContainer := builder.AddAzurePostgresFlexibleServer("resource")
	pgContainer.RunAsContainer(nil)
	_ = pgContainer.AddDatabase("resource", nil)

	app, err := builder.Build()
	if err != nil {
		log.Fatalf("Build: %v", err)
	}
	if err := app.Run(nil); err != nil {
		log.Fatalf("Run: %v", err)
	}
}
