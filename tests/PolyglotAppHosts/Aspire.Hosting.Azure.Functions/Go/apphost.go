// Aspire Go validation AppHost - Aspire.Hosting.Azure.Functions
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

	funcApp, err := builder.AddAzureFunctionsProject("resource", ".")
	if err != nil {
		log.Fatalf("AddAzureFunctionsProject: %v", err)
	}

	storage1, err := builder.AddAzureStorage("resource")
	if err != nil {
		log.Fatalf("AddAzureStorage: %v", err)
	}
	funcApp.WithHostStorage(storage1)

	_, err = builder.AddAzureStorage("resource")
	if err != nil {
		log.Fatalf("AddAzureStorage: %v", err)
	}
	_, _ = funcApp.WithReference(nil, nil, nil, nil)

	app, err := builder.Build()
	if err != nil {
		log.Fatalf("Build: %v", err)
	}
	if err := app.Run(nil); err != nil {
		log.Fatalf("Run: %v", err)
	}
}
