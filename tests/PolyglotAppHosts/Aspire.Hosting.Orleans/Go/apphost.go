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

	orleans := builder.AddOrleans("resource")
	if err = orleans.Err(); err != nil {
		log.Fatalf("orleans: %v", err)
	}
	orService, err := orleans.AsClient()
	if err != nil {
		log.Fatalf("AsClient: %v", err)
	}
	_ = orService

	silo := builder.AddContainer("resource", "image")
	silo.WithOrleansReference(orleans)
	if err = silo.Err(); err != nil {
		log.Fatalf("silo: %v", err)
	}

	client := builder.AddContainer("resource", "image")
	client.WithOrleansClientReference(orService)
	if err = client.Err(); err != nil {
		log.Fatalf("client: %v", err)
	}

	app, err := builder.Build()
	if err != nil {
		log.Fatalf("Build: %v", err)
	}
	if err := app.Run(nil); err != nil {
		log.Fatalf("Run: %v", err)
	}
}
