// Aspire Go validation AppHost - Aspire.Hosting.Azure.Storage
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

	storage, err := builder.AddAzureStorage("resource")
	if err != nil {
		log.Fatalf("AddAzureStorage: %v", err)
	}
	_, _ = storage.RunAsEmulator()
	_, _ = storage.WithStorageRoleAssignments()
	_, _ = storage.AddBlobs("resource")
	_, _ = storage.AddTables("resource")
	_, _ = storage.AddQueues("resource")
	_, _ = storage.AddQueue("resource")
	_, _ = storage.AddBlobContainer("resource")

	app, err := builder.Build()
	if err != nil {
		log.Fatalf("Build: %v", err)
	}
	if err := app.Run(nil); err != nil {
		log.Fatalf("Run: %v", err)
	}
}
