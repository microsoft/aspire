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

	_, _ = builder.AddParameter("parameter")
	_, _ = builder.AddParameter("parameter")

	keycloak, err := builder.AddKeycloak("resource")
	if err != nil {
		log.Fatalf("AddKeycloak: %v", err)
	}

	_, err = builder.AddKeycloak("resource")
	if err != nil {
		log.Fatalf("AddKeycloak: %v", err)
	}

	_, err = builder.AddContainer("resource", "image")
	if err != nil {
		log.Fatalf("AddContainer: %v", err)
	}

	_, _ = keycloak.Name()
	_, _ = keycloak.Entrypoint()
	_, _ = keycloak.ShellExecution()

	app, err := builder.Build()
	if err != nil {
		log.Fatalf("Build: %v", err)
	}
	if err := app.Run(nil); err != nil {
		log.Fatalf("Run: %v", err)
	}
}
