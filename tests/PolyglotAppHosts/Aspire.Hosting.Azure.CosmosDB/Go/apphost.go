// Aspire Go validation AppHost - Aspire.Hosting.Azure.CosmosDB
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

	cosmos, err := builder.AddAzureCosmosDB("resource")
	if err != nil {
		log.Fatalf("AddAzureCosmosDB: %v", err)
	}
	cosmos.WithDefaultAzureSku()

	db, err := cosmos.AddCosmosDatabase("resource", nil)
	if err != nil {
		log.Fatalf("AddCosmosDatabase: %v", err)
	}
	_, _ = db.AddContainer("resource", "image", nil)
	_, _ = db.AddContainerWithPartitionKeyPaths("resource", []string{"/id"}, nil)

	cosmos.WithAccessKeyAuthentication()

	_, err = builder.AddAzureKeyVault("resource")
	if err != nil {
		log.Fatalf("AddAzureKeyVault: %v", err)
	}
	cosmos.WithAccessKeyAuthenticationWithKeyVault(nil)

	cosmosEmulator, err := builder.AddAzureCosmosDB("resource")
	if err != nil {
		log.Fatalf("AddAzureCosmosDB: %v", err)
	}
	cosmosEmulator.RunAsEmulator(nil)

	cosmosPreview, err := builder.AddAzureCosmosDB("resource")
	if err != nil {
		log.Fatalf("AddAzureCosmosDB: %v", err)
	}
	cosmosPreview.RunAsPreviewEmulator(nil)

	app, err := builder.Build()
	if err != nil {
		log.Fatalf("Build: %v", err)
	}
	if err := app.Run(nil); err != nil {
		log.Fatalf("Run: %v", err)
	}
}
