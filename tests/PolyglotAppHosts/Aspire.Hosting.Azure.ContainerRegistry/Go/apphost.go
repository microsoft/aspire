// Aspire Go validation AppHost - Aspire.Hosting.Azure.ContainerRegistry
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

	registry, err := builder.AddAzureContainerRegistry("resource")
	if err != nil {
		log.Fatalf("AddAzureContainerRegistry: %v", err)
	}

	env, err := builder.AddAzureContainerAppEnvironment("resource")
	if err != nil {
		log.Fatalf("AddAzureContainerAppEnvironment: %v", err)
	}
	env.WithAzureContainerRegistry(registry)
	_, _ = env.WithContainerRegistryRoleAssignments(registry, nil)

	registryFromEnv, err := env.GetAzureContainerRegistry()
	if err != nil {
		log.Fatalf("GetAzureContainerRegistry: %v", err)
	}
	registryFromEnv.WithPurgeTask("0 3 * * *", nil, nil, nil, nil)

	app, err := builder.Build()
	if err != nil {
		log.Fatalf("Build: %v", err)
	}
	if err := app.Run(nil); err != nil {
		log.Fatalf("Run: %v", err)
	}
}
