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

	_, _ = builder.AddAzureProvisioning()

	param, err := builder.AddParameter("parameter", nil)
	if err != nil {
		log.Fatalf("AddParameter: %v", err)
	}
	_, _ = builder.AddParameter("parameter", nil)
	_, _ = builder.AddParameter("parameter", nil)
	_, _ = builder.AddParameter("parameter", nil)

	_, _ = builder.AddConnectionString("connection-string", nil)

	azureEnv, err := builder.AddAzureEnvironment()
	if err != nil {
		log.Fatalf("AddAzureEnvironment: %v", err)
	}
	azureEnv.WithLocation(param)

	_, _ = builder.AddContainer("resource", "image")
	_, _ = builder.AddExecutable("resource", "echo", ".", nil)

	fileBicep, err := builder.AddBicepTemplate("resource", "main.bicep")
	if err != nil {
		log.Fatalf("AddBicepTemplate: %v", err)
	}
	_, _ = fileBicep.PublishAsConnectionString()
	_, _ = fileBicep.ClearDefaultRoleAssignments()
	_, _ = fileBicep.GetBicepIdentifier()
	_, _ = fileBicep.IsExisting()
	_, _ = fileBicep.RunAsExisting(nil, nil)
	_, _ = fileBicep.PublishAsExisting(nil, nil)
	_, _ = fileBicep.AsExisting(nil, nil)

	inlineBicep, err := builder.AddBicepTemplateString("resource", "")
	if err != nil {
		log.Fatalf("AddBicepTemplateString: %v", err)
	}
	_, _ = inlineBicep.PublishAsConnectionString()
	_, _ = inlineBicep.ClearDefaultRoleAssignments()
	_, _ = inlineBicep.GetBicepIdentifier()
	_, _ = inlineBicep.IsExisting()

	infra, err := builder.AddAzureInfrastructure("resource", nil)
	if err != nil {
		log.Fatalf("AddAzureInfrastructure: %v", err)
	}
	_, _ = infra.GetOutput("outputName")
	infra.WithParameter("name")
	infra.WithParameterStringValue("name", "")
	infra.WithParameterStringValues("name", nil)
	infra.WithParameterFromParameter("name", nil)
	infra.WithParameterFromConnectionString("name", nil)
	infra.WithParameterFromOutput("name", nil)
	infra.WithParameterFromReferenceExpression("name", nil)
	infra.WithParameterFromEndpoint("name", nil)
	_, _ = infra.PublishAsConnectionString()
	_, _ = infra.ClearDefaultRoleAssignments()
	_, _ = infra.GetBicepIdentifier()
	_, _ = infra.IsExisting()
	_, _ = infra.RunAsExisting(nil, nil)
	_, _ = infra.PublishAsExisting(nil, nil)
	_, _ = infra.AsExisting(nil, nil)

	identity, err := builder.AddAzureUserAssignedIdentity("resource")
	if err != nil {
		log.Fatalf("AddAzureUserAssignedIdentity: %v", err)
	}
	_, _ = identity.ConfigureInfrastructure(nil)
	identity.WithParameter("name")
	identity.WithParameterStringValue("name", "")
	identity.WithParameterStringValues("name", nil)
	identity.WithParameterFromParameter("name", nil)
	identity.WithParameterFromConnectionString("name", nil)
	identity.WithParameterFromOutput("name", nil)
	identity.WithParameterFromReferenceExpression("name", nil)
	identity.WithParameterFromEndpoint("name", nil)
	_, _ = identity.PublishAsConnectionString()
	_, _ = identity.ClearDefaultRoleAssignments()
	_, _ = identity.GetBicepIdentifier()
	_, _ = identity.IsExisting()
	_, _ = identity.RunAsExisting(nil, nil)
	_, _ = identity.PublishAsExisting(nil, nil)
	_, _ = identity.AsExisting(nil, nil)

	app, err := builder.Build()
	if err != nil {
		log.Fatalf("Build: %v", err)
	}
	if err := app.Run(nil); err != nil {
		log.Fatalf("Run: %v", err)
	}
}
