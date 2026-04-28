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

	_ = builder.AddAzureProvisioning()

	location := builder.AddParameter("location")
	if location.Err() != nil {
		log.Fatalf(aspire.FormatError(location.Err()))
	}
	resourceGroup := builder.AddParameter("resource-group")
	if resourceGroup.Err() != nil {
		log.Fatalf(aspire.FormatError(resourceGroup.Err()))
	}
	existingName := builder.AddParameter("existing-name")
	if existingName.Err() != nil {
		log.Fatalf(aspire.FormatError(existingName.Err()))
	}
	existingResourceGroup := builder.AddParameter("existing-resource-group")
	if existingResourceGroup.Err() != nil {
		log.Fatalf(aspire.FormatError(existingResourceGroup.Err()))
	}

	connectionString := builder.AddConnectionString("azure-validation", &aspire.AddConnectionStringOptions{
		EnvironmentVariableNameOrExpression: "AZURE_VALIDATION_CONNECTION_STRING",
	})
	if connectionString.Err() != nil {
		log.Fatalf(aspire.FormatError(connectionString.Err()))
	}

	azureEnvironment := builder.AddAzureEnvironment()
	if azureEnvironment.Err() != nil {
		log.Fatalf(aspire.FormatError(azureEnvironment.Err()))
	}
	azureEnvironment.WithLocation(location).WithResourceGroup(resourceGroup)

	container := builder.AddContainer("api", "mcr.microsoft.com/dotnet/samples:aspnetapp").
		WithHttpEndpoint(&aspire.WithHttpEndpointOptions{
			Name:       aspire.StringPtr("http"),
			TargetPort: aspire.Float64Ptr(8080),
		})
	if container.Err() != nil {
		log.Fatalf(aspire.FormatError(container.Err()))
	}

	executable := builder.AddExecutable("worker", "dotnet", ".", []string{"--info"}).
		WithHttpEndpoint(&aspire.WithHttpEndpointOptions{
			Name:       aspire.StringPtr("http"),
			TargetPort: aspire.Float64Ptr(8081),
		})
	if executable.Err() != nil {
		log.Fatalf(aspire.FormatError(executable.Err()))
	}

	endpoint := container.GetEndpoint("http")

	fileBicep := builder.AddBicepTemplate("file-bicep", "./validation.bicep")
	if fileBicep.Err() != nil {
		log.Fatalf(aspire.FormatError(fileBicep.Err()))
	}
	_ = fileBicep.PublishAsConnectionString()
	_ = fileBicep.ClearDefaultRoleAssignments()
	_, _ = fileBicep.GetBicepIdentifier()
	_, _ = fileBicep.IsExisting()
	_ = fileBicep.RunAsExisting("file-bicep-existing", &aspire.RunAsExistingOptions{
		ResourceGroup: "rg-bicep",
	})
	_ = fileBicep.RunAsExisting(existingName, &aspire.RunAsExistingOptions{
		ResourceGroup: existingResourceGroup,
	})
	_ = fileBicep.PublishAsExisting("file-bicep-existing", &aspire.PublishAsExistingOptions{
		ResourceGroup: "rg-bicep",
	})
	_ = fileBicep.PublishAsExisting(existingName, &aspire.PublishAsExistingOptions{
		ResourceGroup: existingResourceGroup,
	})
	_ = fileBicep.AsExisting(existingName, &aspire.AsExistingOptions{
		ResourceGroup: existingResourceGroup,
	})
	if fileBicep.Err() != nil {
		log.Fatalf(aspire.FormatError(fileBicep.Err()))
	}

	inlineBicep := builder.AddBicepTemplateString("inline-bicep", `
output inlineUrl string = 'https://inline.example.com'
`)
	if inlineBicep.Err() != nil {
		log.Fatalf(aspire.FormatError(inlineBicep.Err()))
	}
	_ = inlineBicep.PublishAsConnectionString()
	_ = inlineBicep.ClearDefaultRoleAssignments()
	_, _ = inlineBicep.GetBicepIdentifier()
	_, _ = inlineBicep.IsExisting()
	if inlineBicep.Err() != nil {
		log.Fatalf(aspire.FormatError(inlineBicep.Err()))
	}

	infra := builder.AddAzureInfrastructure("infra", func(ctx aspire.AzureResourceInfrastructure) {
		_, _ = ctx.BicepName()
		_ = ctx.SetTargetScope(aspire.DeploymentScopeSubscription)
	})
	if infra.Err() != nil {
		log.Fatalf(aspire.FormatError(infra.Err()))
	}

	infrastructureOutput := infra.GetOutput("serviceUrl")
	_, _ = infrastructureOutput.Name()
	_, _ = infrastructureOutput.Value()
	_, _ = infrastructureOutput.ValueExpression()

	infra.WithParameter("empty")
	infra.WithParameter("plain", &aspire.WithParameterOptions{Value: "value"})
	infra.WithParameter("list", &aspire.WithParameterOptions{Value: []string{"one", "two"}})
	infra.WithParameter("fromParam", &aspire.WithParameterOptions{Value: existingName})
	infra.WithParameter("fromConnection", &aspire.WithParameterOptions{Value: connectionString})
	infra.WithParameter("fromOutput", &aspire.WithParameterOptions{Value: infrastructureOutput})
	infra.WithParameter("fromExpression", &aspire.WithParameterOptions{Value: aspire.RefExpr("https://{0}", endpoint)})
	infra.WithParameter("fromEndpoint", &aspire.WithParameterOptions{Value: endpoint})
	_ = infra.PublishAsConnectionString()
	_ = infra.ClearDefaultRoleAssignments()
	_, _ = infra.GetBicepIdentifier()
	_, _ = infra.IsExisting()
	_ = infra.RunAsExisting("infra-existing", &aspire.RunAsExistingOptions{ResourceGroup: "rg-infra"})
	_ = infra.RunAsExisting(existingName, &aspire.RunAsExistingOptions{ResourceGroup: existingResourceGroup})
	_ = infra.PublishAsExisting("infra-existing", &aspire.PublishAsExistingOptions{ResourceGroup: "rg-infra"})
	_ = infra.PublishAsExisting(existingName, &aspire.PublishAsExistingOptions{ResourceGroup: existingResourceGroup})
	_ = infra.AsExisting(existingName, &aspire.AsExistingOptions{ResourceGroup: existingResourceGroup})
	if infra.Err() != nil {
		log.Fatalf(aspire.FormatError(infra.Err()))
	}

	identity := builder.AddAzureUserAssignedIdentity("identity")
	if identity.Err() != nil {
		log.Fatalf(aspire.FormatError(identity.Err()))
	}
	_ = identity.ConfigureInfrastructure(func(ctx aspire.AzureResourceInfrastructure) {
		_, _ = ctx.BicepName()
		_ = ctx.SetTargetScope(aspire.DeploymentScopeSubscription)
	})

	identity.WithParameter("identityEmpty")
	identity.WithParameter("identityPlain", &aspire.WithParameterOptions{Value: "value"})
	identity.WithParameter("identityList", &aspire.WithParameterOptions{Value: []string{"a", "b"}})
	identity.WithParameter("identityFromParam", &aspire.WithParameterOptions{Value: existingName})
	identity.WithParameter("identityFromConnection", &aspire.WithParameterOptions{Value: connectionString})
	identity.WithParameter("identityFromOutput", &aspire.WithParameterOptions{Value: infrastructureOutput})
	identity.WithParameter("identityFromExpression", &aspire.WithParameterOptions{Value: aspire.RefExpr("{0}", location)})
	identity.WithParameter("identityFromEndpoint", &aspire.WithParameterOptions{Value: endpoint})
	_ = identity.PublishAsConnectionString()
	_ = identity.ClearDefaultRoleAssignments()
	_, _ = identity.GetBicepIdentifier()
	_, _ = identity.IsExisting()
	_ = identity.RunAsExisting("identity-existing", &aspire.RunAsExistingOptions{ResourceGroup: "rg-identity"})
	_ = identity.RunAsExisting(existingName, &aspire.RunAsExistingOptions{ResourceGroup: existingResourceGroup})
	_ = identity.PublishAsExisting("identity-existing", &aspire.PublishAsExistingOptions{ResourceGroup: "rg-identity"})
	_ = identity.PublishAsExisting(existingName, &aspire.PublishAsExistingOptions{ResourceGroup: existingResourceGroup})
	_ = identity.AsExisting(existingName, &aspire.AsExistingOptions{ResourceGroup: existingResourceGroup})

	identityClientId := identity.GetOutput("clientId")
	if identity.Err() != nil {
		log.Fatalf(aspire.FormatError(identity.Err()))
	}

	container.WithEnvironment("INFRA_URL", infrastructureOutput)
	container.WithEnvironment("SECRET_FROM_IDENTITY", identityClientId)
	_ = container.WithAzureUserAssignedIdentity(identity)

	executable.WithEnvironment("INFRA_URL", infrastructureOutput)
	executable.WithEnvironment("SECRET_FROM_IDENTITY", identityClientId)
	_ = executable.WithAzureUserAssignedIdentity(identity)

	app, err := builder.Build()
	if err != nil {
		log.Fatalf(aspire.FormatError(err))
	}
	if err := app.Run(nil); err != nil {
		log.Fatalf(aspire.FormatError(err))
	}
}
