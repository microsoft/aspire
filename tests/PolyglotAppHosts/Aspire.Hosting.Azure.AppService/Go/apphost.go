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

	builder.AddParameter("parameter", nil)
	builder.AddParameter("parameter", nil)
	_ = builder.AddAzureApplicationInsights("resource")

	env := builder.AddAzureAppServiceEnvironment("resource")
	_, _ = env.GetResourceName()

	website := builder.AddContainer("resource", "image")
	_, _ = website.GetResourceName()

	_ = builder.AddExecutable("resource", "echo", ".", nil)
	_ = builder.AddProject("resource", ".", "default")

	app, err := builder.Build()
	if err != nil {
		log.Fatalf("Build: %v", err)
	}
	if err := app.Run(nil); err != nil {
		log.Fatalf("Run: %v", err)
	}
}
