package main

import (
	"log"

	"apphost/modules/aspire"
)

func main() {
	builder, err := aspire.CreateBuilder()
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

	environmentIdentifier, err := environment.GetBicepIdentifier()
	if err != nil {
		log.Fatalf(aspire.FormatError(err))
	}
	// The plan's identifier has an _asplan suffix, unlike the hosting resource.
	environment.ConfigureInfrastructure(func(infrastructure aspire.AzureResourceInfrastructure) {
		plan := infrastructure.GetAppServicePlanByIdentifier(environmentIdentifier + "_asplan")
		if err := plan.SetIsPerSiteScaling(true).Err(); err != nil {
			log.Fatalf(aspire.FormatError(err))
		}
		if _, err := plan.IsPerSiteScaling(); err != nil {
			log.Fatalf(aspire.FormatError(err))
		}
		// Keep SKU location metadata out of the deployment's plan SKU.
		sku := infrastructure.CreateAppServiceSkuDescription()
		if err := sku.Locations().Add(infrastructure.CreateAppServiceAzureLocation("westus2")); err != nil {
			log.Fatalf(aspire.FormatError(err))
		}
		location := sku.Locations().Get(0)
		if _, err := location.Name(); err != nil {
			log.Fatalf(aspire.FormatError(err))
		}
	})
	if environment.Err() != nil {
		log.Fatalf(aspire.FormatError(environment.Err()))
	}

	website := builder.AddContainer("frontend", "nginx").
		PublishAsAzureAppServiceWebsite(&aspire.PublishAsAzureAppServiceWebsiteOptions{
			Configure: func(infrastructure aspire.AzureResourceInfrastructure, appService aspire.WebSite) {
				site := infrastructure.GetWebSiteByIdentifier("webapp")
				if err := site.SetIsHttpsOnly(true).Err(); err != nil {
					log.Fatalf(aspire.FormatError(err))
				}
				if _, err := site.IsHttpsOnly(); err != nil {
					log.Fatalf(aspire.FormatError(err))
				}
				if err := appService.ConfigureSiteConfig(&aspire.AzureAppServiceSiteConfig{IsAlwaysOn: aspire.BoolPtr(true)}); err != nil {
					log.Fatalf(aspire.FormatError(err))
				}
			},
			ConfigureSlot: func(_ aspire.AzureResourceInfrastructure, appServiceSlot aspire.WebSiteSlot) {
				if err := appServiceSlot.ConfigureSlotSiteConfig(&aspire.AzureAppServiceSiteConfig{IsAlwaysOn: aspire.BoolPtr(false)}); err != nil {
					log.Fatalf(aspire.FormatError(err))
				}
			},
		}).SkipEnvironmentVariableNameChecks()
	if website.Err() != nil {
		log.Fatalf(aspire.FormatError(website.Err()))
	}

	worker := builder.AddExecutable("worker", "dotnet", ".", []string{"run"}).
		PublishAsAzureAppServiceWebsite(&aspire.PublishAsAzureAppServiceWebsiteOptions{
			Configure: func(_ aspire.AzureResourceInfrastructure, appService aspire.WebSite) {
				if err := appService.ConfigureSiteConfig(&aspire.AzureAppServiceSiteConfig{IsAlwaysOn: aspire.BoolPtr(true)}); err != nil {
					log.Fatalf(aspire.FormatError(err))
				}
			}}).
		SkipEnvironmentVariableNameChecks()
	if worker.Err() != nil {
		log.Fatalf(aspire.FormatError(worker.Err()))
	}

	api := builder.AddProject("api", "../Fake.Api/Fake.Api.csproj", &aspire.AddProjectOptions{LaunchProfileOrOptions: "https"}).
		PublishAsAzureAppServiceWebsite(&aspire.PublishAsAzureAppServiceWebsiteOptions{
			ConfigureSlot: func(_ aspire.AzureResourceInfrastructure, appServiceSlot aspire.WebSiteSlot) {
				if err := appServiceSlot.ConfigureSlotSiteConfig(&aspire.AzureAppServiceSiteConfig{IsAlwaysOn: aspire.BoolPtr(false)}); err != nil {
					log.Fatalf(aspire.FormatError(err))
				}
			}}).
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
	if err := app.Run(); err != nil {
		log.Fatalf(aspire.FormatError(err))
	}
}
