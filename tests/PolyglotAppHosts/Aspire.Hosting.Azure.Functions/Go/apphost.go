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

	funcApp := builder.AddAzureFunctionsProject("resource", ".")

	storage1 := builder.AddAzureStorage("resource")
	funcApp.WithHostStorage(storage1)

	_ = builder.AddAzureStorage("resource")
	_, _ = funcApp.WithReference(nil, nil, nil, nil)

	app, err := builder.Build()
	if err != nil {
		log.Fatalf("Build: %v", err)
	}
	if err := app.Run(nil); err != nil {
		log.Fatalf("Run: %v", err)
	}
}
