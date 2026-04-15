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

	// ── 1. addAzureStorage ────────────────────────────────────────────────────
	storage := builder.AddAzureStorage("storage")

	// ── 2. runAsEmulator (no callback) ───────────────────────────────────────
	storage.RunAsEmulator(nil)

	// ── 3. withStorageRoleAssignments ─────────────────────────────────────────
	storage.WithStorageRoleAssignments(storage, []aspire.AzureStorageRole{
		aspire.AzureStorageRoleStorageBlobDataContributor,
		aspire.AzureStorageRoleStorageQueueDataContributor,
	})

	// ── 4. addBlobs / addTables / addQueues ───────────────────────────────────
	storage.AddBlobs("blobs")
	storage.AddTables("tables")
	storage.AddQueues("queues")

	// ── 5. addQueue (single queue resource) ───────────────────────────────────
	storage.AddQueue("orders")

	// ── 6. addBlobContainer ───────────────────────────────────────────────────
	storage.AddBlobContainer("images")

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
