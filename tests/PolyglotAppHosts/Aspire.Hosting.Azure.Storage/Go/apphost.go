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

	storage := builder.AddAzureStorage("resource")
	storage.RunAsEmulator(nil)
	storage.WithStorageRoleAssignments(storage, nil)
	storage.AddBlobs("resource")
	storage.AddTables("resource")
	storage.AddQueues("resource")
	storage.AddQueue("resource", nil)
	storage.AddBlobContainer("resource", nil)
	if err = storage.Err(); err != nil {
		log.Fatalf("storage: %v", err)
	}

	app, err := builder.Build()
	if err != nil {
		log.Fatalf("Build: %v", err)
	}
	if err := app.Run(nil); err != nil {
		log.Fatalf("Run: %v", err)
	}
}
