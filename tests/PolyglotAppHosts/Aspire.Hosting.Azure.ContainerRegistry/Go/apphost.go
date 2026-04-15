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

	// ── 1. addAzureContainerRegistry + withPurgeTask ─────────────────────────
	registry := builder.AddAzureContainerRegistry("containerregistry").
		WithPurgeTaskWithOpts("0 1 * * *", &aspire.WithPurgeTaskOptions{
			Filter:   aspire.StringPtr("samples:*"),
			Ago:      aspire.Float64Ptr(7),
			Keep:     aspire.Float64Ptr(5),
			TaskName: aspire.StringPtr("purge-samples"),
		})
	if err = registry.Err(); err != nil {
		log.Fatalf("registry: %v", err)
	}

	// ── 2. addAzureContainerAppEnvironment + role assignments ─────────────────
	environment := builder.AddAzureContainerAppEnvironment("environment")
	environment.WithAzureContainerRegistry(registry)
	environment.WithContainerRegistryRoleAssignments(registry, []aspire.AzureContainerRegistryRole{
		aspire.AzureContainerRegistryRoleAcrPull,
		aspire.AzureContainerRegistryRoleAcrPush,
	})

	// ── 3. getAzureContainerRegistry + withPurgeTask on retrieved registry ────
	registryFromEnvironment := environment.GetAzureContainerRegistry()
	registryFromEnvironment.WithPurgeTaskWithOpts("0 2 * * *", &aspire.WithPurgeTaskOptions{
		Filter: aspire.StringPtr("environment:*"),
		Ago:    aspire.Float64Ptr(14),
		Keep:   aspire.Float64Ptr(2),
	})

	app, err := builder.Build()
	if err != nil {
		log.Fatalf("Build: %v", err)
	}
	if err := app.Run(nil); err != nil {
		log.Fatalf("Run: %v", err)
	}
}
