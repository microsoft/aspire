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

	applicationInsightsLocation := builder.AddParameter("applicationInsightsLocation")
	deploymentSlot := builder.AddParameter("deploymentSlot")
	existingApplicationInsights := builder.AddAzureApplicationInsights("existingApplicationInsights")

	environment := builder.AddAzureAppServiceEnvironment("appservice-environment").
		WithDashboard().
		WithDashboard(&aspire.WithDashboardOptions{Enable: aspire.BoolPtr(false)}).
		WithAzureApplicationInsights().
		WithAzureApplicationInsights(&aspire.WithAzureApplicationInsightsOptions{ApplicationInsights: existingApplicationInsights}).
		WithDeploymentSlot(applicationInsightsLocation).
		WithDeploymentSlot(deploymentSlot).
		WithDeploymentSlot("staging")
	if environment.Err() != nil {
		log.Fatalf(aspire.FormatError(environment.Err()))
	}

	website := builder.AddContainer("frontend", "nginx").
		PublishAsAzureAppServiceWebsite(&aspire.PublishAsAzureAppServiceWebsiteOptions{
			Configure:     func(_ aspire.AzureResourceInfrastructure, _ aspire.WebSite) {},
			ConfigureSlot: func(_ aspire.AzureResourceInfrastructure, _ aspire.WebSiteSlot) {},
		}).SkipEnvironmentVariableNameChecks()
	if website.Err() != nil {
		log.Fatalf(aspire.FormatError(website.Err()))
	}

	worker := builder.AddExecutable("worker", "dotnet", ".", []string{"run"}).
		PublishAsAzureAppServiceWebsite(&aspire.PublishAsAzureAppServiceWebsiteOptions{
			Configure: func(_ aspire.AzureResourceInfrastructure, _ aspire.WebSite) {}}).
		SkipEnvironmentVariableNameChecks()
	if worker.Err() != nil {
		log.Fatalf(aspire.FormatError(worker.Err()))
	}

	api := builder.AddProject("api", "../Fake.Api/Fake.Api.csproj", &aspire.AddProjectOptions{LaunchProfileOrOptions: "https"}).
		PublishAsAzureAppServiceWebsite(&aspire.PublishAsAzureAppServiceWebsiteOptions{
			Configure: func(_ aspire.AzureResourceInfrastructure, _ aspire.WebSite) {}}).
		SkipEnvironmentVariableNameChecks()
	if api.Err() != nil {
		log.Fatalf(aspire.FormatError(api.Err()))
	}

	_, _ = environment.GetResourceName()
	_, _ = website.GetResourceName()

	app, err := builder.Build()
	if err != nil {
		log.Fatalf(aspire.FormatError(err))
	}
	if err := app.Run(nil); err != nil {
		log.Fatalf(aspire.FormatError(err))
	}
}
