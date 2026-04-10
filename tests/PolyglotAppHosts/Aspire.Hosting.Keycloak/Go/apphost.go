// Aspire Go validation AppHost - Aspire.Hosting.Keycloak
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

	builder.AddParameter("parameter", nil)
	builder.AddParameter("parameter", nil)

	keycloak := builder.AddKeycloak("resource", nil, nil, nil)
	_, _ = keycloak.Name()
	_, _ = keycloak.Entrypoint()
	_, _ = keycloak.ShellExecution()
	if err = keycloak.Err(); err != nil {
		log.Fatalf("keycloak: %v", err)
	}

	builder.AddKeycloak("resource", nil, nil, nil)

	builder.AddContainer("resource", "image")

	app, err := builder.Build()
	if err != nil {
		log.Fatalf("Build: %v", err)
	}
	if err := app.Run(nil); err != nil {
		log.Fatalf("Run: %v", err)
	}
}
