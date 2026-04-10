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

	cosmos := builder.AddAzureCosmosDB("resource")
	cosmos.WithDefaultAzureSku()

	db := cosmos.AddCosmosDatabase("resource", nil)
	_ = db.AddContainer("resource", "image", nil)
	_ = db.AddContainerWithPartitionKeyPaths("resource", []string{"/id"}, nil)

	cosmos.WithAccessKeyAuthentication()

	_ = builder.AddAzureKeyVault("resource")
	cosmos.WithAccessKeyAuthenticationWithKeyVault(nil)

	cosmosEmulator := builder.AddAzureCosmosDB("resource")
	cosmosEmulator.RunAsEmulator(nil)

	cosmosPreview := builder.AddAzureCosmosDB("resource")
	cosmosPreview.RunAsPreviewEmulator(nil)

	app, err := builder.Build()
	if err != nil {
		log.Fatalf("Build: %v", err)
	}
	if err := app.Run(nil); err != nil {
		log.Fatalf("Run: %v", err)
	}
}
