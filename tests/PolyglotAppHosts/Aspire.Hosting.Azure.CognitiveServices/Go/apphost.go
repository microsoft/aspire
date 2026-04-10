// Aspire Go validation AppHost - Aspire.Hosting.Azure.CognitiveServices
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

	openai, err := builder.AddAzureOpenAI("resource")
	if err != nil {
		log.Fatalf("AddAzureOpenAI: %v", err)
	}
	_, _ = openai.AddDeployment("resource")

	api, err := builder.AddContainer("resource", "image")
	if err != nil {
		log.Fatalf("AddContainer: %v", err)
	}
	_, _ = api.WithCognitiveServicesRoleAssignments()

	app, err := builder.Build()
	if err != nil {
		log.Fatalf("Build: %v", err)
	}
	if err := app.Run(nil); err != nil {
		log.Fatalf("Run: %v", err)
	}
}
