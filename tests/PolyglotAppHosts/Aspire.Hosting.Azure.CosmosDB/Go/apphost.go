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

	// ── 1. addAzureCosmosDB ──────────────────────────────────────────────────
	cosmos := builder.AddAzureCosmosDB("cosmos")

	// ── 2. withDefaultAzureSku ───────────────────────────────────────────────
	cosmos.WithDefaultAzureSku()

	// ── 3. addCosmosDatabase (simple) + addCosmosDatabaseWithOpts ────────────
	db := cosmos.AddCosmosDatabase("app-db")
	_ = cosmos.AddCosmosDatabaseWithOpts("app-db-named", &aspire.AddCosmosDatabaseOptions{
		DatabaseName: aspire.StringPtr("appdb"),
	})

	// ── 4. addContainer (simple) + addContainerWithOpts ───────────────────────
	_ = db.AddContainer("orders", "/orderId")
	_ = db.AddContainerWithOpts("orders-named", "/orderId", &aspire.AzureCosmosDBDatabaseResourceAddContainerOptions{
		ContainerName: aspire.StringPtr("orders-container"),
	})

	// ── 5. addContainerWithPartitionKeyPaths (simple) + WithOpts ─────────────
	_ = db.AddContainerWithPartitionKeyPaths("events", []string{"/tenantId", "/eventId"})
	_ = db.AddContainerWithPartitionKeyPathsWithOpts("events-named", []string{"/tenantId", "/eventId"},
		&aspire.AddContainerWithPartitionKeyPathsOptions{
			ContainerName: aspire.StringPtr("events-container"),
		})

	// ── 6. withAccessKeyAuthentication ───────────────────────────────────────
	cosmos.WithAccessKeyAuthentication()

	// ── 7. withAccessKeyAuthenticationWithKeyVault ───────────────────────────
	keyVault := builder.AddAzureKeyVault("kv")
	cosmos.WithAccessKeyAuthenticationWithKeyVault(
		aspire.NewIAzureKeyVaultResource(keyVault.Handle(), keyVault.Client()))

	// ── 8. runAsEmulator (typed callback) ─────────────────────────────────────
	cosmosEmulator := builder.AddAzureCosmosDB("cosmos-emulator")
	cosmosEmulator.RunAsEmulator(func(emulator *aspire.AzureCosmosDBEmulatorResource) {
		emulator.WithDataVolumeWithOpts(&aspire.WithDataVolumeOptions{
			Name: aspire.StringPtr("cosmos-emulator-data"),
		})
		emulator.WithGatewayPort(18081)
		emulator.WithPartitionCount(25)
	})

	// ── 9. runAsPreviewEmulator (typed callback) ───────────────────────────────
	cosmosPreview := builder.AddAzureCosmosDB("cosmos-preview-emulator")
	cosmosPreview.RunAsPreviewEmulator(func(emulator *aspire.AzureCosmosDBEmulatorResource) {
		emulator.WithDataExplorerWithOpts(&aspire.WithDataExplorerOptions{
			Port: aspire.Float64Ptr(11234),
		})
	})

	app, err := builder.Build()
	if err != nil {
		log.Fatalf("Build: %v", err)
	}
	if err := app.Run(nil); err != nil {
		log.Fatalf("Run: %v", err)
	}
}
