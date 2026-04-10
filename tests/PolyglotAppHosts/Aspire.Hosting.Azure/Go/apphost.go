// Aspire Go validation AppHost - Aspire.Hosting.Azure
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

	_, _ = builder.AddAzureProvisioning("resource")

	_, _ = builder.AddParameter("parameter")
	_, _ = builder.AddParameter("parameter")
	_, _ = builder.AddParameter("parameter")
	_, _ = builder.AddParameter("parameter")

	_, _ = builder.AddConnectionString("connection-string", nil)

	azureEnv, err := builder.AddAzureEnvironment("resource")
	if err != nil {
		log.Fatalf("AddAzureEnvironment: %v", err)
	}
	_, _ = azureEnv.WithLocation()

	_, _ = builder.AddContainer("resource", "image")
	_, _ = builder.AddExecutable("resource", "echo", ".", nil)

	fileBicep, err := builder.AddBicepTemplate("resource")
	if err != nil {
		log.Fatalf("AddBicepTemplate: %v", err)
	}
	_, _ = fileBicep.PublishAsConnectionString()
	_, _ = fileBicep.ClearDefaultRoleAssignments()
	_, _ = fileBicep.GetBicepIdentifier()
	_, _ = fileBicep.IsExisting()
	_, _ = fileBicep.RunAsExisting()
	_, _ = fileBicep.RunAsExistingFromParameters()
	_, _ = fileBicep.PublishAsExisting()
	_, _ = fileBicep.PublishAsExistingFromParameters()
	_, _ = fileBicep.AsExisting()

	inlineBicep, err := builder.AddBicepTemplateString("resource")
	if err != nil {
		log.Fatalf("AddBicepTemplateString: %v", err)
	}
	_, _ = inlineBicep.PublishAsConnectionString()
	_, _ = inlineBicep.ClearDefaultRoleAssignments()
	_, _ = inlineBicep.GetBicepIdentifier()
	_, _ = inlineBicep.IsExisting()

	infra, err := builder.AddAzureInfrastructure("resource")
	if err != nil {
		log.Fatalf("AddAzureInfrastructure: %v", err)
	}
	_, _ = infra.GetOutput()
	_, _ = infra.WithParameter()
	_, _ = infra.WithParameterStringValue()
	_, _ = infra.WithParameterStringValues()
	_, _ = infra.WithParameterFromParameter()
	_, _ = infra.WithParameterFromConnectionString()
	_, _ = infra.WithParameterFromOutput()
	_, _ = infra.WithParameterFromReferenceExpression()
	_, _ = infra.WithParameterFromEndpoint()
	_, _ = infra.PublishAsConnectionString()
	_, _ = infra.ClearDefaultRoleAssignments()
	_, _ = infra.GetBicepIdentifier()
	_, _ = infra.IsExisting()
	_, _ = infra.RunAsExisting()
	_, _ = infra.RunAsExistingFromParameters()
	_, _ = infra.PublishAsExisting()
	_, _ = infra.PublishAsExistingFromParameters()
	_, _ = infra.AsExisting()

	identity, err := builder.AddAzureUserAssignedIdentity("resource")
	if err != nil {
		log.Fatalf("AddAzureUserAssignedIdentity: %v", err)
	}
	_, _ = identity.ConfigureInfrastructure()
	_, _ = identity.WithParameter()
	_, _ = identity.WithParameterStringValue()
	_, _ = identity.WithParameterStringValues()
	_, _ = identity.WithParameterFromParameter()
	_, _ = identity.WithParameterFromConnectionString()
	_, _ = identity.WithParameterFromOutput()
	_, _ = identity.WithParameterFromReferenceExpression()
	_, _ = identity.WithParameterFromEndpoint()
	_, _ = identity.PublishAsConnectionString()
	_, _ = identity.ClearDefaultRoleAssignments()
	_, _ = identity.GetBicepIdentifier()
	_, _ = identity.IsExisting()
	_, _ = identity.RunAsExisting()
	_, _ = identity.RunAsExistingFromParameters()
	_, _ = identity.PublishAsExisting()
	_, _ = identity.PublishAsExistingFromParameters()
	_, _ = identity.AsExisting()

	app, err := builder.Build()
	if err != nil {
		log.Fatalf("Build: %v", err)
	}
	if err := app.Run(nil); err != nil {
		log.Fatalf("Run: %v", err)
	}
}
