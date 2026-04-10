// Aspire Go validation AppHost - Aspire.Hosting.Foundry
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

	foundry, err := builder.AddFoundry("resource")
	if err != nil {
		log.Fatalf("AddFoundry: %v", err)
	}
	_, _ = foundry.AddDeploymentFromModel("resource", nil)

	localFoundry, err := builder.AddFoundry("resource")
	if err != nil {
		log.Fatalf("AddFoundry: %v", err)
	}
	_, _ = localFoundry.AddDeployment("resource", "model", "version", "format")

	registry, err := builder.AddAzureContainerRegistry("resource")
	if err != nil {
		log.Fatalf("AddAzureContainerRegistry: %v", err)
	}
	vault, err := builder.AddAzureKeyVault("resource")
	if err != nil {
		log.Fatalf("AddAzureKeyVault: %v", err)
	}
	appInsights, err := builder.AddAzureApplicationInsights("resource")
	if err != nil {
		log.Fatalf("AddAzureApplicationInsights: %v", err)
	}
	cosmos, err := builder.AddAzureCosmosDB("resource")
	if err != nil {
		log.Fatalf("AddAzureCosmosDB: %v", err)
	}
	azStorage, err := builder.AddAzureStorage("resource")
	if err != nil {
		log.Fatalf("AddAzureStorage: %v", err)
	}

	project, err := foundry.AddProject("resource")
	if err != nil {
		log.Fatalf("AddProject: %v", err)
	}
	project.WithContainerRegistry(registry)
	project.WithKeyVault(vault)
	project.WithAppInsights(appInsights)
	_, _ = project.AddCosmosConnection(cosmos)
	_, _ = project.AddStorageConnection(azStorage)
	_, _ = project.AddContainerRegistryConnection(registry)
	_, _ = project.AddKeyVaultConnection(vault)

	builderProjectFoundry, err := builder.AddFoundry("resource")
	if err != nil {
		log.Fatalf("AddFoundry: %v", err)
	}
	builderProject, err := builderProjectFoundry.AddProject("resource")
	if err != nil {
		log.Fatalf("AddProject: %v", err)
	}
	_, _ = builderProject.AddModelDeployment("resource", "model", "version", "format")

	_, _ = project.AddModelDeploymentFromModel("resource", nil)

	hostedAgent, err := builder.AddExecutable("resource", "echo", ".", nil)
	if err != nil {
		log.Fatalf("AddExecutable: %v", err)
	}
	hostedAgent.PublishAsHostedAgent(nil, nil)

	api, err := builder.AddContainer("resource", "image")
	if err != nil {
		log.Fatalf("AddContainer: %v", err)
	}
	_, _ = api.WithRoleAssignments(foundry, nil)

	app, err := builder.Build()
	if err != nil {
		log.Fatalf("Build: %v", err)
	}
	if err := app.Run(nil); err != nil {
		log.Fatalf("Run: %v", err)
	}
}
