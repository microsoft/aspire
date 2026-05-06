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

	registry := builder.AddAzureContainerRegistry("containerregistry").
		WithPurgeTask("0 1 * * *", &aspire.WithPurgeTaskOptions{
			Filter:   aspire.StringPtr("samples:*"),
			Ago:      aspire.Float64Ptr(7),
			Keep:     aspire.Float64Ptr(5),
			TaskName: aspire.StringPtr("purge-samples"),
		})
	if err = registry.Err(); err != nil {
		log.Fatalf(aspire.FormatError(err))
	}

	environment := builder.AddAzureContainerAppEnvironment("environment")
	environment.WithAzureContainerRegistry(registry)
	environment.WithRoleAssignments(registry, []aspire.AzureContainerRegistryRole{
		aspire.AzureContainerRegistryRoleAcrPull,
		aspire.AzureContainerRegistryRoleAcrPush,
	})
	if err = environment.Err(); err != nil {
		log.Fatalf(aspire.FormatError(err))
	}

	registryFromEnvironment := environment.GetAzureContainerRegistry()
	registryFromEnvironment.WithPurgeTask("0 2 * * *", &aspire.WithPurgeTaskOptions{
		Filter: aspire.StringPtr("environment:*"),
		Ago:    aspire.Float64Ptr(14),
		Keep:   aspire.Float64Ptr(2),
	})
	if err = registryFromEnvironment.Err(); err != nil {
		log.Fatalf(aspire.FormatError(err))
	}

	app, err := builder.Build()
	if err != nil {
		log.Fatalf("Build: %v", err)
	}
	if err := app.Run(nil); err != nil {
		log.Fatalf("Run: %v", err)
	}
}
