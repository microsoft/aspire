package main

import (
	"log"

	"apphost/modules/aspire"
)

func main() {
	builder, err := aspire.CreateBuilder()
	if err != nil {
		log.Fatal(aspire.FormatError(err))
	}

	registry := builder.AddAzureContainerRegistry("containerregistry").
		WithPurgeTask("0 1 * * *", &aspire.WithPurgeTaskOptions{
			Filter:   aspire.StringPtr("samples:*"),
			Ago:      aspire.IntPtr(7),
			Keep:     aspire.IntPtr(5),
			TaskName: aspire.StringPtr("purge-samples"),
		})
	_ = registry.ConfigureInfrastructure(func(infrastructure aspire.AzureResourceInfrastructure) {
		service := infrastructure.GetContainerRegistryService()
		service.Tags().Set("provisioning-proxy", "go")
	})
	if err = registry.Err(); err != nil {
		log.Fatal(aspire.FormatError(err))
	}

	environment := builder.AddAzureContainerAppEnvironment("environment")
	environment.WithAzureContainerRegistry(registry)
	environment.WithContainerRegistryRoleAssignments(registry, []aspire.AzureContainerRegistryRole{
		aspire.AzureContainerRegistryRoleAcrPull,
		aspire.AzureContainerRegistryRoleAcrPush,
	})
	if err = environment.Err(); err != nil {
		log.Fatal(aspire.FormatError(err))
	}

	registryFromEnvironment := environment.GetAzureContainerRegistry()
	registryFromEnvironment.WithPurgeTask("0 2 * * *", &aspire.WithPurgeTaskOptions{
		Filter: aspire.StringPtr("environment:*"),
		Ago:    aspire.IntPtr(14),
		Keep:   aspire.IntPtr(2),
	})
	if err = registryFromEnvironment.Err(); err != nil {
		log.Fatal(aspire.FormatError(err))
	}

	app, err := builder.Build()
	if err != nil {
		log.Fatalf("Build: %v", err)
	}
	if err := app.Run(); err != nil {
		log.Fatalf("Run: %v", err)
	}
}
