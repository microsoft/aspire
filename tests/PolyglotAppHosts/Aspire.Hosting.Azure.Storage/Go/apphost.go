package main

import (
	"log"

	"apphost/modules/aspire"
)

func main() {
	builder, err := aspire.CreateBuilder()
	if err != nil {
		log.Fatalf(aspire.FormatError(err))
	}

	storage := builder.AddAzureStorage("storage")
	_ = storage.ConfigureInfrastructure(func(infrastructure aspire.AzureResourceInfrastructure) {
		account := infrastructure.GetStorageAccount()
		account.Tags().Set("provisioning-proxy", "go")
		immutabilityPolicy := infrastructure.CreateAccountImmutabilityPolicy()
		immutabilityPolicy.SetImmutabilityPeriodSinceCreationInDays(float64(30))
	})
	if storage.Err() != nil {
		log.Fatalf(aspire.FormatError(storage.Err()))
	}

	storage.RunAsEmulator()
	if storage.Err() != nil {
		log.Fatalf(aspire.FormatError(storage.Err()))
	}

	storage.WithStorageRoleAssignments(storage, []aspire.AzureStorageRole{
		aspire.AzureStorageRoleStorageBlobDataContributor,
		aspire.AzureStorageRoleStorageQueueDataContributor,
	})

	storage.AddBlobs("blobs")
	storage.AddTables("tables")
	storage.AddQueues("queues")
	storage.AddQueue("orders")
	storage.AddBlobContainer("images")

	if storage.Err() != nil {
		log.Fatalf(aspire.FormatError(storage.Err()))
	}

	app, err := builder.Build()
	if err != nil {
		log.Fatalf(aspire.FormatError(err))
	}
	if err := app.Run(); err != nil {
		log.Fatalf(aspire.FormatError(err))
	}
}
