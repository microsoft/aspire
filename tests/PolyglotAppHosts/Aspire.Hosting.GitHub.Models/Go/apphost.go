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

	githubModel := builder.AddGitHubModel("resource", aspire.GitHubModelName("gpt-4o"), nil)

	param := builder.AddParameter("parameter", nil)
	if err = param.Err(); err != nil {
		log.Fatalf("param: %v", err)
	}
	builder.AddGitHubModel("resource", aspire.GitHubModelName("gpt-4o"), nil)

	builder.AddGitHubModelById("resource", "model-id", nil)

	builder.AddParameter("parameter", nil)
	githubModel.WithApiKey(param)
	githubModel.EnableHealthCheck()
	if err = githubModel.Err(); err != nil {
		log.Fatalf("githubModel: %v", err)
	}

	container := builder.AddContainer("resource", "image")
	container.WithReference(nil, nil, nil, nil)
	container.WithReference(nil, nil, nil, nil)
	if err = container.Err(); err != nil {
		log.Fatalf("container: %v", err)
	}

	app, err := builder.Build()
	if err != nil {
		log.Fatalf("Build: %v", err)
	}
	if err := app.Run(nil); err != nil {
		log.Fatalf("Run: %v", err)
	}
}
