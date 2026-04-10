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

	builder.AddAzureProvisioning()

	param := builder.AddParameter("parameter", nil)
	if err = param.Err(); err != nil {
		log.Fatalf("param: %v", err)
	}
	builder.AddParameter("parameter", nil)
	builder.AddParameter("parameter", nil)
	builder.AddParameter("parameter", nil)

	builder.AddConnectionString("connection-string", nil)

	azureEnv := builder.AddAzureEnvironment()
	azureEnv.WithLocation(param)
	if err = azureEnv.Err(); err != nil {
		log.Fatalf("azureEnv: %v", err)
	}

	builder.AddContainer("resource", "image")
	builder.AddExecutable("resource", "echo", ".", nil)

	fileBicep := builder.AddBicepTemplate("resource", "main.bicep")
	_, _ = fileBicep.PublishAsConnectionString()
	_, _ = fileBicep.ClearDefaultRoleAssignments()
	_, _ = fileBicep.GetBicepIdentifier()
	_, _ = fileBicep.IsExisting()
	_, _ = fileBicep.RunAsExisting(nil, nil)
	_, _ = fileBicep.PublishAsExisting(nil, nil)
	_, _ = fileBicep.AsExisting(nil, nil)
	if err = fileBicep.Err(); err != nil {
		log.Fatalf("fileBicep: %v", err)
	}

	inlineBicep := builder.AddBicepTemplateString("resource", "")
	_, _ = inlineBicep.PublishAsConnectionString()
	_, _ = inlineBicep.ClearDefaultRoleAssignments()
	_, _ = inlineBicep.GetBicepIdentifier()
	_, _ = inlineBicep.IsExisting()
	if err = inlineBicep.Err(); err != nil {
		log.Fatalf("inlineBicep: %v", err)
	}

	infra := builder.AddAzureInfrastructure("resource", nil)
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
	if err = infra.Err(); err != nil {
		log.Fatalf("infra: %v", err)
	}

	identity := builder.AddAzureUserAssignedIdentity("resource")
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
	if err = identity.Err(); err != nil {
		log.Fatalf("identity: %v", err)
	}

	app, err := builder.Build()
	if err != nil {
		log.Fatalf("Build: %v", err)
	}
	if err := app.Run(nil); err != nil {
		log.Fatalf("Run: %v", err)
	}
}
