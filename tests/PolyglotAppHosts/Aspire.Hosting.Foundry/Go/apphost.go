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
	_, _ = foundry.AddDeploymentFromModel("resource")

	localFoundry, err := builder.AddFoundry("resource")
	if err != nil {
		log.Fatalf("AddFoundry: %v", err)
	}
	_, _ = localFoundry.AddDeployment("resource")

	_, _ = builder.AddAzureContainerRegistry("resource")
	_, _ = builder.AddAzureKeyVault("resource")
	_, _ = builder.AddAzureApplicationInsights("resource")
	_, _ = builder.AddAzureCosmosDB("resource")
	_, _ = builder.AddAzureStorage("resource")

	project, err := foundry.AddProject("resource", ".", "default")
	if err != nil {
		log.Fatalf("AddProject: %v", err)
	}
	_, _ = project.WithContainerRegistry()
	_, _ = project.WithKeyVault()
	_, _ = project.WithAppInsights()
	_, _ = project.AddCosmosConnection("resource")
	_, _ = project.AddStorageConnection("resource")
	_, _ = project.AddContainerRegistryConnection("resource")
	_, _ = project.AddKeyVaultConnection("resource")

	builderProjectFoundry, err := builder.AddFoundry("resource")
	if err != nil {
		log.Fatalf("AddFoundry: %v", err)
	}
	builderProject, err := builderProjectFoundry.AddProject("resource", ".", "default")
	if err != nil {
		log.Fatalf("AddProject: %v", err)
	}
	_, _ = builderProject.AddModelDeployment("resource")

	_, _ = project.AddModelDeploymentFromModel("resource")
	_, _ = project.AddAndPublishPromptAgent("resource")

	hostedAgent, err := builder.AddExecutable("resource", "echo", ".", nil)
	if err != nil {
		log.Fatalf("AddExecutable: %v", err)
	}
	_, _ = hostedAgent.PublishAsHostedAgent()

	api, err := builder.AddContainer("resource", "image")
	if err != nil {
		log.Fatalf("AddContainer: %v", err)
	}
	_, _ = api.WithRoleAssignments()

	_, _ = foundry.DeploymentName()
	_, _ = foundry.ModelName()
	_, _ = foundry.Format()
	_, _ = foundry.ModelVersion()
	_, _ = foundry.ConnectionStringExpression()

	app, err := builder.Build()
	if err != nil {
		log.Fatalf("Build: %v", err)
	}
	if err := app.Run(nil); err != nil {
		log.Fatalf("Run: %v", err)
	}
}
