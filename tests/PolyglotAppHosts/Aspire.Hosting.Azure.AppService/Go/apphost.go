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

	applicationInsightsLocation := builder.AddParameter("applicationInsightsLocation")
	deploymentSlot := builder.AddParameter("deploymentSlot")
	existingApplicationInsights := builder.AddAzureApplicationInsights("existingApplicationInsights")

	environment := builder.AddAzureAppServiceEnvironment("appservice-environment").
		WithDashboard().
		WithDashboardWithOpts(&aspire.WithDashboardOptions{Enable: aspire.BoolPtr(false)}).
		WithAzureApplicationInsights().
		WithAzureApplicationInsightsLocation("westus").
		WithAzureApplicationInsightsLocationParameter(applicationInsightsLocation).
		WithAzureApplicationInsightsResource(existingApplicationInsights).
		WithDeploymentSlotParameter(deploymentSlot).
		WithDeploymentSlot("staging")

	website := builder.AddContainer("frontend", "nginx")
	_, err = website.PublishAsAzureAppServiceWebsite(
		func(_ *aspire.AzureResourceInfrastructure, _ *aspire.WebSite) {},
		func(_ *aspire.AzureResourceInfrastructure, _ *aspire.WebSiteSlot) {},
	)
	if err != nil {
		log.Fatalf("website.PublishAsAzureAppServiceWebsite: %v", err)
	}
	_, err = website.SkipEnvironmentVariableNameChecks()
	if err != nil {
		log.Fatalf("website.SkipEnvironmentVariableNameChecks: %v", err)
	}

	worker := builder.AddExecutable("worker", "dotnet", ".", []string{"run"})
	_, err = worker.PublishAsAzureAppServiceWebsite(
		func(_ *aspire.AzureResourceInfrastructure, _ *aspire.WebSite) {},
		nil,
	)
	if err != nil {
		log.Fatalf("worker.PublishAsAzureAppServiceWebsite: %v", err)
	}
	_, err = worker.SkipEnvironmentVariableNameChecks()
	if err != nil {
		log.Fatalf("worker.SkipEnvironmentVariableNameChecks: %v", err)
	}

	api := builder.AddProject("api", "../Fake.Api/Fake.Api.csproj", "https")
	_, err = api.PublishAsAzureAppServiceWebsite(
		nil,
		func(_ *aspire.AzureResourceInfrastructure, _ *aspire.WebSiteSlot) {},
	)
	if err != nil {
		log.Fatalf("api.PublishAsAzureAppServiceWebsite: %v", err)
	}
	_, err = api.SkipEnvironmentVariableNameChecks()
	if err != nil {
		log.Fatalf("api.SkipEnvironmentVariableNameChecks: %v", err)
	}

	_, _ = environment.GetResourceName()
	_, _ = website.GetResourceName()

	app, err := builder.Build()
	if err != nil {
		log.Fatalf("Build: %v", err)
	}
	if err := app.Run(nil); err != nil {
		log.Fatalf("Run: %v", err)
	}
}
