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

	githubModel, err := builder.AddGitHubModel("resource", aspire.GitHubModelName("gpt-4o"), nil)
	if err != nil {
		log.Fatalf("AddGitHubModel: %v", err)
	}

	param, err := builder.AddParameter("parameter", nil)
	if err != nil {
		log.Fatalf("AddParameter: %v", err)
	}
	_, err = builder.AddGitHubModel("resource", aspire.GitHubModelName("gpt-4o"), nil)
	if err != nil {
		log.Fatalf("AddGitHubModel: %v", err)
	}

	_, err = builder.AddGitHubModelById("resource", "model-id", nil)
	if err != nil {
		log.Fatalf("AddGitHubModelById: %v", err)
	}

	_, _ = builder.AddParameter("parameter", nil)
	githubModel.WithApiKey(param)
	githubModel.EnableHealthCheck()

	container, err := builder.AddContainer("resource", "image")
	if err != nil {
		log.Fatalf("AddContainer: %v", err)
	}
	_, _ = container.WithReference(nil, nil, nil, nil)
	_, _ = container.WithReference(nil, nil, nil, nil)

	app, err := builder.Build()
	if err != nil {
		log.Fatalf("Build: %v", err)
	}
	if err := app.Run(nil); err != nil {
		log.Fatalf("Run: %v", err)
	}
}
