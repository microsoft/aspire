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

	foundry := builder.AddFoundry("foundry")
	foundry.AddDeploymentFromModel("resource", nil)
	if err = foundry.Err(); err != nil {
		log.Fatalf("foundry: %v", err)
	}

	localFoundry := builder.AddFoundry("resource")
	localFoundry.AddDeployment("resource", "model", "version", "format")
	if err = localFoundry.Err(); err != nil {
		log.Fatalf("localFoundry: %v", err)
	}

	registry := builder.AddAzureContainerRegistry("resource")
	if err = registry.Err(); err != nil {
		log.Fatalf("registry: %v", err)
	}
	vault := builder.AddAzureKeyVault("resource")
	if err = vault.Err(); err != nil {
		log.Fatalf("vault: %v", err)
	}
	appInsights := builder.AddAzureApplicationInsights("resource")
	if err = appInsights.Err(); err != nil {
		log.Fatalf("appInsights: %v", err)
	}
	cosmos := builder.AddAzureCosmosDB("resource")
	if err = cosmos.Err(); err != nil {
		log.Fatalf("cosmos: %v", err)
	}
	azStorage := builder.AddAzureStorage("resource")
	if err = azStorage.Err(); err != nil {
		log.Fatalf("azStorage: %v", err)
	}

	project := foundry.AddProject("resource")
	project.WithContainerRegistry(registry)
	project.WithKeyVault(vault)
	project.WithAppInsights(appInsights)
	project.AddCosmosConnection(cosmos)
	project.AddStorageConnection(azStorage)
	project.AddContainerRegistryConnection(registry)
	project.AddKeyVaultConnection(vault)
	project.AddModelDeploymentFromModel("resource", nil)
	if err = project.Err(); err != nil {
		log.Fatalf("project: %v", err)
	}

	builderProjectFoundry := builder.AddFoundry("resource")
	builderProject := builderProjectFoundry.AddProject("resource")
	builderProject.AddModelDeployment("resource", "model", "version", "format")
	if err = builderProject.Err(); err != nil {
		log.Fatalf("builderProject: %v", err)
	}

	hostedAgent := builder.AddExecutable("resource", "echo", ".", nil)
	hostedAgent.PublishAsHostedAgent(nil, nil)
	if err = hostedAgent.Err(); err != nil {
		log.Fatalf("hostedAgent: %v", err)
	}

	api := builder.AddContainer("resource", "image")
	api.WithRoleAssignments(foundry, nil)
	if err = api.Err(); err != nil {
		log.Fatalf("api: %v", err)
	}

	app, err := builder.Build()
	if err != nil {
		log.Fatalf("Build: %v", err)
	}
	if err := app.Run(nil); err != nil {
		log.Fatalf("Run: %v", err)
	}
}
