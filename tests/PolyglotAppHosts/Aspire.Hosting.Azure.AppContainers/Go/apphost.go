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

	// === Azure Container App Environment ===
	// Test AddAzureContainerAppEnvironment factory method
	env := builder.AddAzureContainerAppEnvironment("myenv")
	if err := env.Err(); err != nil {
		log.Fatalf(aspire.FormatError(err))
	}

	// Test fluent chaining on AzureContainerAppEnvironmentResource
	env.WithAzdResourceNaming().
		WithCompactResourceNaming().
		WithDashboard(&aspire.WithDashboardOptions{Enable: aspire.BoolPtr(true)}).
		WithHttpsUpgrade(&aspire.WithHttpsUpgradeOptions{Upgrade: aspire.BoolPtr(false)})
	if err := env.Err(); err != nil {
		log.Fatalf(aspire.FormatError(err))
	}

	// Test WithDashboard with no args (uses default)
	env2 := builder.AddAzureContainerAppEnvironment("myenv2")
	env2.WithDashboard()
	if err := env2.Err(); err != nil {
		log.Fatalf(aspire.FormatError(err))
	}

	// Test WithHttpsUpgrade with no args (uses default)
	env2.WithHttpsUpgrade()
	if err := env2.Err(); err != nil {
		log.Fatalf(aspire.FormatError(err))
	}

	// === WithAzureLogAnalyticsWorkspace ===
	// Test WithAzureLogAnalyticsWorkspace with a Log Analytics Workspace resource
	laws := builder.AddAzureLogAnalyticsWorkspace("laws")
	env3 := builder.AddAzureContainerAppEnvironment("myenv3").
		WithAzureLogAnalyticsWorkspace(laws)
	if err := env3.Err(); err != nil {
		log.Fatalf(aspire.FormatError(err))
	}
	customDomain := builder.AddParameter("customDomain")
	certificateName := builder.AddParameter("certificateName")

	// === PublishAsAzureContainerApp ===
	// Test PublishAsAzureContainerApp on a container resource with callback
	web := builder.AddContainer("web", "myregistry/web:latest")
	web.PublishAsAzureContainerApp(func(infra aspire.AzureResourceInfrastructure, app aspire.ContainerApp) {
		err := app.ConfigureCustomDomain(customDomain, certificateName)
		if err != nil {
			log.Fatalf(aspire.FormatError(err))
		}
	})

	// Test PublishAsAzureContainerAppJob on an executable resource
	api := builder.AddExecutable("api", "dotnet", ".", []string{"run"}).
		PublishAsAzureContainerAppJob()
	if err := api.Err(); err != nil {
		log.Fatalf(aspire.FormatError(err))
	}

	// === PublishAsAzureContainerAppJob ===
	// Test PublishAsAzureContainerAppJob (parameterless - manual trigger)
	worker := builder.AddContainer("worker", "myregistry/worker:latest").
		PublishAsAzureContainerAppJob()
	if err := worker.Err(); err != nil {
		log.Fatalf(aspire.FormatError(err))
	}

	// Test PublishAsAzureContainerAppJob (with callback)
	processor := builder.AddContainer("processor", "myregistry/processor:latest").
		PublishAsAzureContainerAppJob(
			&aspire.PublishAsAzureContainerAppJobOptions{
				Configure: func(_ aspire.AzureResourceInfrastructure, _ aspire.ContainerAppJob) {
					// Configure the container app job here
				},
			})
	if err := processor.Err(); err != nil {
		log.Fatalf(aspire.FormatError(err))
	}

	// Test publishAsScheduledAzureContainerAppJob (simple - no callback)
	scheduler := builder.AddContainer("scheduler", "myregistry/scheduler:latest").
		PublishAsScheduledAzureContainerAppJob("0 0 * * *")
	if err := scheduler.Err(); err != nil {
		log.Fatalf(aspire.FormatError(err))
	}

	// Test PublishAsScheduledAzureContainerAppJob (with callback)
	reporter := builder.AddContainer("reporter", "myregistry/reporter:latest").
		PublishAsScheduledAzureContainerAppJob("0 */6 * * *",
			&aspire.PublishAsScheduledAzureContainerAppJobOptions{
				Configure: func(_ aspire.AzureResourceInfrastructure, _ aspire.ContainerAppJob) {
					// Configure the scheduled job here
				},
			})
	if err := reporter.Err(); err != nil {
		log.Fatalf(aspire.FormatError(err))
	}

	app, err := builder.Build()
	if err != nil {
		log.Fatalf(aspire.FormatError(err))
	}
	if err := app.Run(nil); err != nil {
		log.Fatalf(aspire.FormatError(err))
	}
}
