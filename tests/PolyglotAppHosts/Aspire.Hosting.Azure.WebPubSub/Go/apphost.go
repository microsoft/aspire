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

	webpubsub := builder.AddAzureWebPubSub("resource")

	hub := webpubsub.AddHub("resource", nil)

	webpubsub.AddHub("resource", nil)

	hub.AddEventHandler(nil, nil, nil)
	hub.AddEventHandler(nil, nil, nil)
	if err = hub.Err(); err != nil {
		log.Fatalf("hub: %v", err)
	}

	webpubsub.WithWebPubSubRoleAssignments(webpubsub, nil)
	if err = webpubsub.Err(); err != nil {
		log.Fatalf("webpubsub: %v", err)
	}

	container := builder.AddContainer("resource", "image")
	container.WithWebPubSubRoleAssignments(webpubsub, nil)
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
