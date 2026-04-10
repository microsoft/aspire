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
	_, _ = env2.WithDashboard()
	_, _ = env2.WithHttpsUpgrade()

	laws, err := builder.AddAzureLogAnalyticsWorkspace("resource")
	if err != nil {
		log.Fatalf("AddAzureLogAnalyticsWorkspace: %v", err)
	}
	env3, err := builder.AddAzureContainerAppEnvironment("resource")
	if err != nil {
		log.Fatalf("AddAzureContainerAppEnvironment: %v", err)
	}
	_, _ = env3.WithAzureLogAnalyticsWorkspace(laws)

	_, _ = builder.AddParameter("parameter")
	_, _ = builder.AddParameter("parameter")

	web, err := builder.AddContainer("resource", "image")
	if err != nil {
		log.Fatalf("AddContainer: %v", err)
	}
	_, _ = web.PublishAsAzureContainerApp(func(args ...any) any { return nil })

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
	_, _ = processor.PublishAsConfiguredAzureContainerAppJob()

	scheduler, err := builder.AddContainer("resource", "image")
	if err != nil {
		log.Fatalf("AddContainer: %v", err)
	}
	_, _ = scheduler.PublishAsScheduledAzureContainerAppJob()

	reporter, err := builder.AddContainer("resource", "image")
	if err != nil {
		log.Fatalf("AddContainer: %v", err)
	}
	_, _ = reporter.PublishAsConfiguredScheduledAzureContainerAppJob()

	app, err := builder.Build()
	if err != nil {
		log.Fatalf("Build: %v", err)
	}
	if err := app.Run(nil); err != nil {
		log.Fatalf("Run: %v", err)
	}
}
