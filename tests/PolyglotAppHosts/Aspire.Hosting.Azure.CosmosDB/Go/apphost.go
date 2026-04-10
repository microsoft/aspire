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
	_, _ = cosmos.WithDefaultAzureSku()

	db, err := cosmos.AddCosmosDatabase("resource")
	if err != nil {
		log.Fatalf("AddCosmosDatabase: %v", err)
	}
	_, _ = db.AddContainer("resource", "image")
	_, _ = db.AddContainerWithPartitionKeyPaths("resource")

	_, _ = cosmos.WithAccessKeyAuthentication()
	_, _ = builder.AddAzureKeyVault("resource")
	_, _ = cosmos.WithAccessKeyAuthenticationWithKeyVault()

	cosmosEmulator, err := builder.AddAzureCosmosDB("resource")
	if err != nil {
		log.Fatalf("AddAzureCosmosDB: %v", err)
	}
	_, _ = cosmosEmulator.RunAsEmulator()

	cosmosPreview, err := builder.AddAzureCosmosDB("resource")
	if err != nil {
		log.Fatalf("AddAzureCosmosDB: %v", err)
	}
	_, _ = cosmosPreview.RunAsPreviewEmulator()

	app, err := builder.Build()
	if err != nil {
		log.Fatalf("Build: %v", err)
	}
	if err := app.Run(nil); err != nil {
		log.Fatalf("Run: %v", err)
	}
}
