// Aspire Go validation AppHost - Aspire.Hosting.Azure.WebPubSub
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

	webpubsub, err := builder.AddAzureWebPubSub("resource")
	if err != nil {
		log.Fatalf("AddAzureWebPubSub: %v", err)
	}

	hub, err := webpubsub.AddHub("resource")
	if err != nil {
		log.Fatalf("AddHub: %v", err)
	}

	_, err = webpubsub.AddHub("resource")
	if err != nil {
		log.Fatalf("AddHub: %v", err)
	}

	_, _ = hub.AddEventHandler("resource")
	_, _ = hub.AddEventHandler("resource")

	container, err := builder.AddContainer("resource", "image")
	if err != nil {
		log.Fatalf("AddContainer: %v", err)
	}
	_, _ = container.WithWebPubSubRoleAssignments()
	_, _ = webpubsub.WithWebPubSubRoleAssignments()
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
