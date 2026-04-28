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

	storage := builder.AddAzureStorage("storage")
	if storage.Err() != nil {
		log.Fatalf(aspire.FormatError(storage.Err()))
	}

	storage.RunAsEmulator()
	if storage.Err() != nil {
		log.Fatalf(aspire.FormatError(storage.Err()))
	}

	storage.WithRoleAssignments(storage, []aspire.AzureStorageRole{
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
	if err := app.Run(nil); err != nil {
		log.Fatalf(aspire.FormatError(err))
	}
}
