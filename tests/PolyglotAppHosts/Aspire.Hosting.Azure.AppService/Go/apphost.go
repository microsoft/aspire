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
		// Mutate a detached connection model; networking output lists stay read-only.
		connection := infrastructure.CreateRemotePrivateEndpointConnection()
		addresses := connection.IPAddresses()
		if err := addresses.Add("192.0.2.1"); err != nil {
			log.Fatalf(aspire.FormatError(err))
		}
		if err := addresses.Insert(0, "2001:db8::1"); err != nil {
			log.Fatalf(aspire.FormatError(err))
		}
		if err := addresses.Set(1, "192.0.2.2"); err != nil {
			log.Fatalf(aspire.FormatError(err))
		}
		literal := addresses.Get(0)
		if err := addresses.Set(1, literal); err != nil {
			log.Fatalf(aspire.FormatError(err))
		}
		bicep := infrastructure.Bicep()
		// Index creates an untyped expression rather than a typed BicepValue<string>.
		expression := bicep.Index(bicep.ParseJson(bicep.String(`["192.0.2.3"]`)), float64(0))
		if err := addresses.Add(expression); err != nil {
			log.Fatalf(aspire.FormatError(err))
		}
		if err := addresses.Insert(1, expression); err != nil {
			log.Fatalf(aspire.FormatError(err))
		}
		if err := addresses.Set(0, addresses.Get(3)); err != nil {
			log.Fatalf(aspire.FormatError(err))
		}
		if count, err := addresses.Count(); err != nil || count != 4 {
			log.Fatalf("IP address count = %v, error = %v; want 4", count, err)
		}
		for index, expected := range []aspire.BicepValueKind{
			aspire.BicepValueKindExpression, aspire.BicepValueKindExpression,
			aspire.BicepValueKindLiteral, aspire.BicepValueKindExpression,
		} {
			if kind, err := addresses.Get(float64(index)).Kind(); err != nil || kind != expected {
				log.Fatalf("IP address %d kind = %v, error = %v; want %v", index, kind, err, expected)
			}
		}
		networking := infrastructure.CreateAseV3NetworkingConfigurationData()
		for _, output := range []interface{ Count() (float64, error) }{
			networking.ExternalInboundIPAddresses(), networking.InternalInboundIPAddresses(),
			networking.LinuxOutboundIPAddresses(), networking.WindowsOutboundIPAddresses(),
		} {
			if count, err := output.Count(); err != nil || count != 0 {
				log.Fatalf("Detached networking output count = %v, error = %v; want 0", count, err)
			}
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
