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

	_ = builder.AddAzureContainerAppEnvironment("resource")

	env2 := builder.AddAzureContainerAppEnvironment("resource").WithDashboard(nil).WithHttpsUpgrade(nil)
	if err = env2.Err(); err != nil {
		log.Fatalf("env2: %v", err)
	}

	laws := builder.AddAzureLogAnalyticsWorkspace("resource")
	env3 := builder.AddAzureContainerAppEnvironment("resource").WithAzureLogAnalyticsWorkspace(laws)
	if err = env3.Err(); err != nil {
		log.Fatalf("env3: %v", err)
	}

	builder.AddParameter("parameter", nil)
	builder.AddParameter("parameter", nil)

	web := builder.AddContainer("resource", "image").PublishAsAzureContainerApp(func(args ...any) any { return nil })
	if err = web.Err(); err != nil {
		log.Fatalf("web: %v", err)
	}

	api := builder.AddExecutable("resource", "echo", ".", nil)
	_, _ = api.PublishAsAzureContainerAppJob()

	worker := builder.AddContainer("resource", "image")
	_, _ = worker.PublishAsAzureContainerAppJob()

	processor := builder.AddContainer("resource", "image")
	_, _ = processor.PublishAsConfiguredAzureContainerAppJob(nil)

	scheduler := builder.AddContainer("resource", "image")
	_, _ = scheduler.PublishAsScheduledAzureContainerAppJob("0 * * * *")

	reporter := builder.AddContainer("resource", "image")
	_, _ = reporter.PublishAsConfiguredScheduledAzureContainerAppJob("0 * * * *", nil)

	app, err := builder.Build()
	if err != nil {
		log.Fatalf("Build: %v", err)
	}
	if err := app.Run(nil); err != nil {
		log.Fatalf("Run: %v", err)
	}
}
