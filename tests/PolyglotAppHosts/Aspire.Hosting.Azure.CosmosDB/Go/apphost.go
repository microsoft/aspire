package main

import (
	"log"

	"apphost/modules/aspire"
)

func main() {
	builder, err := aspire.CreateBuilder(nil)
	if err != nil {
		log.Fatalf(aspire.FormatError(err))
	}

	// 1) AddAzureCosmosDB
	cosmos := builder.AddAzureCosmosDB("cosmos")

	// 2) WithDefaultAzureSku
	cosmos.WithDefaultAzureSku()
	if cosmos.Err() != nil {
		log.Fatalf(aspire.FormatError(cosmos.Err()))
	}

	// 3) AddCosmosDatabase
	db := cosmos.AddCosmosDatabase("app-db", &aspire.AddCosmosDatabaseOptions{
		DatabaseName: aspire.StringPtr("appdb"),
	})
	if db.Err() != nil {
		log.Fatalf(aspire.FormatError(db.Err()))
	}

	// 4) AddContainer (single partition key path)
	container := db.AddContainer("orders", "/orderId", &aspire.AzureCosmosDBDatabaseResourceAddContainerOptions{
		ContainerName: aspire.StringPtr("orders-container"),
	})
	if container.Err() != nil {
		log.Fatalf(aspire.FormatError(container.Err()))
	}

	// 5) AddContainer (IEnumerable<string> export)
	_ = db.AddContainer("events", []string{"/tenantId", "/eventId"},
		&aspire.AzureCosmosDBDatabaseResourceAddContainerOptions{
			ContainerName: aspire.StringPtr("events-container"),
		})

	// 6) WithAccessKeyAuthentication
	cosmos.WithAccessKeyAuthentication()

	// 7) WithAccessKeyAuthentication with Key Vault
	keyVault := builder.AddAzureKeyVault("kv")
	var keyVaultResource aspire.AzureKeyVaultResource = keyVault
	cosmos.WithAccessKeyAuthentication(&aspire.WithAccessKeyAuthenticationOptions{KeyVaultBuilder: &keyVaultResource})

	// 8) RunAsEmulator + emulator container configuration methods
	cosmosEmulator := builder.AddAzureCosmosDB("cosmos-emulator")
	cosmosEmulator.RunAsEmulator(&aspire.RunAsEmulatorOptions{
		ConfigureContainer: func(emulator aspire.AzureCosmosDBEmulatorResource) {
			emulator.WithDataVolume(&aspire.WithDataVolumeOptions{
				Name: aspire.StringPtr("cosmos-emulator-data"),
			}) // 9) WithDataVolume
			emulator.WithGatewayPort(18081) // 10) WithGatewayPort
			emulator.WithPartitionCount(25) // 11) WithPartitionCount
		},
	})
	if cosmosEmulator.Err() != nil {
		log.Fatalf(aspire.FormatError(cosmosEmulator.Err()))
	}

	// 12) RunAsPreviewEmulator + 13) WithDataExplorer
	cosmosPreview := builder.AddAzureCosmosDB("cosmos-preview-emulator")
	cosmosPreview.RunAsPreviewEmulator(&aspire.RunAsPreviewEmulatorOptions{
		ConfigureContainer: func(emulator aspire.AzureCosmosDBEmulatorResource) {
			emulator.WithDataExplorer(&aspire.WithDataExplorerOptions{
				Port: aspire.Float64Ptr(11234),
			})
		},
	})
	if cosmosPreview.Err() != nil {
		log.Fatalf(aspire.FormatError(cosmosPreview.Err()))
	}

	app, err := builder.Build()
	if err != nil {
		log.Fatalf(aspire.FormatError(err))
	}
	if err := app.Run(nil); err != nil {
		log.Fatalf(aspire.FormatError(err))
	}
}
