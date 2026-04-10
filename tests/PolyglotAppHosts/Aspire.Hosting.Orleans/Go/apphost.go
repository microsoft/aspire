// Aspire Go validation AppHost - Aspire.Hosting.Orleans
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

	_, _ = builder.AddConnectionString("connection-string", nil)

	orleans, err := builder.AddOrleans("resource")
	if err != nil {
		log.Fatalf("AddOrleans: %v", err)
	}
	orService, err := orleans.AsClient()
	if err != nil {
		log.Fatalf("AsClient: %v", err)
	}
	_ = orService

	silo, err := builder.AddContainer("resource", "image")
	if err != nil {
		log.Fatalf("AddContainer: %v", err)
	}
	_, _ = silo.WithOrleansReference(orleans)

	client, err := builder.AddContainer("resource", "image")
	if err != nil {
		log.Fatalf("AddContainer: %v", err)
	}
	_, _ = client.WithOrleansClientReference(orService)

	app, err := builder.Build()
	if err != nil {
		log.Fatalf("Build: %v", err)
	}
	if err := app.Run(nil); err != nil {
		log.Fatalf("Run: %v", err)
	}
}
