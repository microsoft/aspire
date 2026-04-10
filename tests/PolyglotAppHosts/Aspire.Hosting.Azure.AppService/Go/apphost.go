// Aspire Go validation AppHost - Aspire.Hosting.Azure.AppService
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

	_, _ = builder.AddParameter("parameter")
	_, _ = builder.AddParameter("parameter")
	_, _ = builder.AddAzureApplicationInsights("resource")

	env, err := builder.AddAzureAppServiceEnvironment("resource")
	if err != nil {
		log.Fatalf("AddAzureAppServiceEnvironment: %v", err)
	}
	_, _ = env.GetResourceName()

	website, err := builder.AddContainer("resource", "image")
	if err != nil {
		log.Fatalf("AddContainer: %v", err)
	}
	_, _ = website.GetResourceName()

	_, _ = builder.AddExecutable("resource", "echo", ".", nil)
	_, _ = builder.AddProject("resource", ".", "default")

	app, err := builder.Build()
	if err != nil {
		log.Fatalf("Build: %v", err)
	}
	if err := app.Run(nil); err != nil {
		log.Fatalf("Run: %v", err)
	}
}
