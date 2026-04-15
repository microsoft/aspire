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

	// ── 1. addAzureProvisioning ───────────────────────────────────────────────
	_, _ = builder.AddAzureProvisioning()

	// ── 2. parameters ─────────────────────────────────────────────────────────
	location := builder.AddParameter("location")
	resourceGroup := builder.AddParameter("resource-group")
	existingName := builder.AddParameter("existing-name")
	existingResourceGroup := builder.AddParameter("existing-resource-group")

	// ── 3. addConnectionString with environmentVariableName ───────────────────
	connectionString := builder.AddConnectionStringWithOpts("azure-validation",
		&aspire.AddConnectionStringOptions{
			EnvironmentVariableName: aspire.StringPtr("AZURE_VALIDATION_CONNECTION_STRING"),
		})

	// ── 4. addAzureEnvironment + withLocation + withResourceGroup ─────────────
	azureEnvironment := builder.AddAzureEnvironment()
	azureEnvironment.WithLocation(location).WithResourceGroup(resourceGroup)

	// ── 5. addContainer + addExecutable ───────────────────────────────────────
	container := builder.AddContainer("api", "mcr.microsoft.com/dotnet/samples:aspnetapp")
	container.WithHttpEndpointWithOpts(&aspire.WithHttpEndpointOptions{
		Name:       aspire.StringPtr("http"),
		TargetPort: aspire.Float64Ptr(8080),
	})
	executable := builder.AddExecutable("worker", "dotnet", ".", []string{"--info"})
	executable.WithHttpEndpointWithOpts(&aspire.WithHttpEndpointOptions{
		Name:       aspire.StringPtr("http"),
		TargetPort: aspire.Float64Ptr(8081),
	})
	endpoint, err := container.GetEndpoint("http")
	if err != nil {
		log.Fatalf("GetEndpoint: %v", err)
	}

	// ── 6. addBicepTemplate ──────────────────────────────────────────────────
	fileBicep := builder.AddBicepTemplate("file-bicep", "./validation.bicep")
	_ = fileBicep.PublishAsConnectionString()
	_ = fileBicep.ClearDefaultRoleAssignments()
	_, _ = fileBicep.GetBicepIdentifier()
	_, _ = fileBicep.IsExisting()

	// runAsExisting (simple + WithOpts)
	_ = fileBicep.RunAsExisting("file-bicep-existing")
	_ = fileBicep.RunAsExistingWithOpts("file-bicep-existing", &aspire.RunAsExistingOptions{
		ResourceGroup: "rg-bicep",
	})
	_ = fileBicep.RunAsExistingWithOpts(existingName, &aspire.RunAsExistingOptions{
		ResourceGroup: existingResourceGroup,
	})

	// publishAsExisting (simple + WithOpts)
	_ = fileBicep.PublishAsExisting("file-bicep-existing")
	_ = fileBicep.PublishAsExistingWithOpts("file-bicep-existing", &aspire.PublishAsExistingOptions{
		ResourceGroup: "rg-bicep",
	})
	_ = fileBicep.PublishAsExistingWithOpts(existingName, &aspire.PublishAsExistingOptions{
		ResourceGroup: existingResourceGroup,
	})

	// asExisting (simple + WithOpts)
	_ = fileBicep.AsExisting(existingName)
	_ = fileBicep.AsExistingWithOpts(existingName, &aspire.AsExistingOptions{
		ResourceGroup: existingResourceGroup,
	})
	if err = fileBicep.Err(); err != nil {
		log.Fatalf("fileBicep: %v", err)
	}

	// ── 7. addBicepTemplateString ─────────────────────────────────────────────
	inlineBicep := builder.AddBicepTemplateString("inline-bicep", `
output inlineUrl string = 'https://inline.example.com'
`)
	_ = inlineBicep.PublishAsConnectionString()
	_ = inlineBicep.ClearDefaultRoleAssignments()
	_, _ = inlineBicep.GetBicepIdentifier()
	_, _ = inlineBicep.IsExisting()
	if err = inlineBicep.Err(); err != nil {
		log.Fatalf("inlineBicep: %v", err)
	}

	// ── 8. addAzureInfrastructure (typed callback) ────────────────────────────
	infra := builder.AddAzureInfrastructure("infra", func(ctx *aspire.AzureResourceInfrastructure) {
		_, _ = ctx.BicepName()
		_, _ = ctx.SetTargetScope(aspire.DeploymentScopeSubscription)
	})

	// getOutput + output properties
	infrastructureOutput, _ := infra.GetOutput("serviceUrl")
	if infrastructureOutput != nil {
		_, _ = infrastructureOutput.Name()
		_, _ = infrastructureOutput.Value()
		_, _ = infrastructureOutput.ValueExpression()
	}

	// withParameter* methods
	infra.WithParameter("empty")
	infra.WithParameterStringValue("plain", "value")
	infra.WithParameterStringValues("list", []string{"one", "two"})
	infra.WithParameterFromParameter("fromParam", existingName)
	infra.WithParameterFromConnectionString("fromConnection", connectionString)
	if infrastructureOutput != nil {
		infra.WithParameterFromOutput("fromOutput", infrastructureOutput)
	}
	infra.WithParameterFromReferenceExpression("fromExpression", aspire.RefExpr("https://%s", endpoint))
	infra.WithParameterFromEndpoint("fromEndpoint", endpoint)

	// publishAsConnectionString / clearDefaultRoleAssignments / etc.
	_ = infra.PublishAsConnectionString()
	_ = infra.ClearDefaultRoleAssignments()
	_, _ = infra.GetBicepIdentifier()
	_, _ = infra.IsExisting()
	_ = infra.RunAsExisting("infra-existing")
	_ = infra.RunAsExistingWithOpts("infra-existing", &aspire.RunAsExistingOptions{ResourceGroup: "rg-infra"})
	_ = infra.RunAsExistingWithOpts(existingName, &aspire.RunAsExistingOptions{ResourceGroup: existingResourceGroup})
	_ = infra.PublishAsExisting("infra-existing")
	_ = infra.PublishAsExistingWithOpts("infra-existing", &aspire.PublishAsExistingOptions{ResourceGroup: "rg-infra"})
	_ = infra.PublishAsExistingWithOpts(existingName, &aspire.PublishAsExistingOptions{ResourceGroup: existingResourceGroup})
	_ = infra.AsExisting(existingName)
	_ = infra.AsExistingWithOpts(existingName, &aspire.AsExistingOptions{ResourceGroup: existingResourceGroup})
	if err = infra.Err(); err != nil {
		log.Fatalf("infra: %v", err)
	}

	// ── 9. addAzureUserAssignedIdentity ──────────────────────────────────────
	identity := builder.AddAzureUserAssignedIdentity("identity")
	_ = identity.ConfigureInfrastructure(func(ctx *aspire.AzureResourceInfrastructure) {
		_, _ = ctx.BicepName()
		_, _ = ctx.SetTargetScope(aspire.DeploymentScopeSubscription)
	})

	identity.WithParameter("identityEmpty")
	identity.WithParameterStringValue("identityPlain", "value")
	identity.WithParameterStringValues("identityList", []string{"a", "b"})
	identity.WithParameterFromParameter("identityFromParam", existingName)
	identity.WithParameterFromConnectionString("identityFromConnection", connectionString)
	if infrastructureOutput != nil {
		identity.WithParameterFromOutput("identityFromOutput", infrastructureOutput)
	}
	identity.WithParameterFromReferenceExpression("identityFromExpression",
		aspire.RefExpr("%s", location))
	identity.WithParameterFromEndpoint("identityFromEndpoint", endpoint)

	_ = identity.PublishAsConnectionString()
	_ = identity.ClearDefaultRoleAssignments()
	_, _ = identity.GetBicepIdentifier()
	_, _ = identity.IsExisting()
	_ = identity.RunAsExisting("identity-existing")
	_ = identity.RunAsExistingWithOpts("identity-existing", &aspire.RunAsExistingOptions{ResourceGroup: "rg-identity"})
	_ = identity.RunAsExistingWithOpts(existingName, &aspire.RunAsExistingOptions{ResourceGroup: existingResourceGroup})
	_ = identity.PublishAsExisting("identity-existing")
	_ = identity.PublishAsExistingWithOpts("identity-existing", &aspire.PublishAsExistingOptions{ResourceGroup: "rg-identity"})
	_ = identity.PublishAsExistingWithOpts(existingName, &aspire.PublishAsExistingOptions{ResourceGroup: existingResourceGroup})
	_ = identity.AsExisting(existingName)
	_ = identity.AsExistingWithOpts(existingName, &aspire.AsExistingOptions{ResourceGroup: existingResourceGroup})

	identityClientId, _ := identity.GetOutput("clientId")
	if err = identity.Err(); err != nil {
		log.Fatalf("identity: %v", err)
	}

	// ── 10. container / executable environment + identity ─────────────────────
	if infrastructureOutput != nil {
		container.WithEnvironment("INFRA_URL", infrastructureOutput)
	}
	if identityClientId != nil {
		container.WithEnvironment("SECRET_FROM_IDENTITY", identityClientId)
	}
	_, _ = container.WithAzureUserAssignedIdentity(identity)

	if infrastructureOutput != nil {
		executable.WithEnvironment("INFRA_URL", infrastructureOutput)
	}
	if identityClientId != nil {
		executable.WithEnvironment("SECRET_FROM_IDENTITY", identityClientId)
	}
	_, _ = executable.WithAzureUserAssignedIdentity(identity)

	app, err := builder.Build()
	if err != nil {
		log.Fatalf("Build: %v", err)
	}
	if err := app.Run(nil); err != nil {
		log.Fatalf("Run: %v", err)
	}
}
