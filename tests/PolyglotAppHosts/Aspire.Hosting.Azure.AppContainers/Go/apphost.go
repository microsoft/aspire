// Aspire Go validation AppHost - Aspire.Hosting.Azure.AppContainers
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

	_, err = builder.AddAzureContainerAppEnvironment("resource")
	if err != nil {
		log.Fatalf("AddAzureContainerAppEnvironment: %v", err)
	}

	env2, err := builder.AddAzureContainerAppEnvironment("resource")
	if err != nil {
		log.Fatalf("AddAzureContainerAppEnvironment: %v", err)
	}
	if err := env2.WithDashboard(nil).WithHttpsUpgrade(nil).Err(); err != nil {
		log.Fatalf("env2 setup: %v", err)
	}

	laws, err := builder.AddAzureLogAnalyticsWorkspace("resource")
	if err != nil {
		log.Fatalf("AddAzureLogAnalyticsWorkspace: %v", err)
	}
	env3, err := builder.AddAzureContainerAppEnvironment("resource")
	if err != nil {
		log.Fatalf("AddAzureContainerAppEnvironment: %v", err)
	}
	if err := env3.WithAzureLogAnalyticsWorkspace(laws).Err(); err != nil {
		log.Fatalf("env3 setup: %v", err)
	}

	_, _ = builder.AddParameter("parameter", nil)
	_, _ = builder.AddParameter("parameter", nil)

	web, err := builder.AddContainer("resource", "image")
	if err != nil {
		log.Fatalf("AddContainer: %v", err)
	}
	if err := web.PublishAsAzureContainerApp(func(args ...any) any { return nil }).Err(); err != nil {
		log.Fatalf("PublishAsAzureContainerApp: %v", err)
	}

	api, err := builder.AddExecutable("resource", "echo", ".", nil)
	if err != nil {
		log.Fatalf("AddExecutable: %v", err)
	}
	_, _ = api.PublishAsAzureContainerAppJob()

	worker, err := builder.AddContainer("resource", "image")
	if err != nil {
		log.Fatalf("AddContainer: %v", err)
	}
	_, _ = worker.PublishAsAzureContainerAppJob()

	processor, err := builder.AddContainer("resource", "image")
	if err != nil {
		log.Fatalf("AddContainer: %v", err)
	}
	_, _ = processor.PublishAsConfiguredAzureContainerAppJob(nil)

	scheduler, err := builder.AddContainer("resource", "image")
	if err != nil {
		log.Fatalf("AddContainer: %v", err)
	}
	_, _ = scheduler.PublishAsScheduledAzureContainerAppJob("0 * * * *")

	reporter, err := builder.AddContainer("resource", "image")
	if err != nil {
		log.Fatalf("AddContainer: %v", err)
	}
	_, _ = reporter.PublishAsConfiguredScheduledAzureContainerAppJob("0 * * * *", nil)

	app, err := builder.Build()
	if err != nil {
		log.Fatalf("Build: %v", err)
	}
	if err := app.Run(nil); err != nil {
		log.Fatalf("Run: %v", err)
	}
}
