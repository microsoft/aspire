// Aspire Go validation AppHost - Aspire.Hosting.GitHub.Models
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

	githubModel, err := builder.AddGitHubModel("resource")
	if err != nil {
		log.Fatalf("AddGitHubModel: %v", err)
	}

	_, _ = builder.AddParameter("parameter")
	_, err = builder.AddGitHubModel("resource")
	if err != nil {
		log.Fatalf("AddGitHubModel: %v", err)
	}

	_, err = builder.AddGitHubModelById("resource")
	if err != nil {
		log.Fatalf("AddGitHubModelById: %v", err)
	}

	_, _ = builder.AddParameter("parameter")
	_, _ = githubModel.WithApiKey()
	_, _ = githubModel.EnableHealthCheck()

	container, err := builder.AddContainer("resource", "image")
	if err != nil {
		log.Fatalf("AddContainer: %v", err)
	}
	_, _ = container.WithReference()
	_, _ = container.WithReference()

	app, err := builder.Build()
	if err != nil {
		log.Fatalf("Build: %v", err)
	}
	if err := app.Run(nil); err != nil {
		log.Fatalf("Run: %v", err)
	}
}
