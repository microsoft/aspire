// Aspire Go validation AppHost - Aspire.Hosting.Azure.EventHubs
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

	eventHubs, err := builder.AddAzureEventHubs("resource")
	if err != nil {
		log.Fatalf("AddAzureEventHubs: %v", err)
	}
	_, _ = eventHubs.WithEventHubsRoleAssignments(eventHubs, nil)

	hub, err := eventHubs.AddHub("resource", nil)
	if err != nil {
		log.Fatalf("AddHub: %v", err)
	}
	hub.WithProperties(nil)

	consumerGroup, err := hub.AddConsumerGroup("resource", nil)
	if err != nil {
		log.Fatalf("AddConsumerGroup: %v", err)
	}
	_, _ = consumerGroup.WithEventHubsRoleAssignments(eventHubs, nil)

	eventHubs.RunAsEmulator(nil)

	app, err := builder.Build()
	if err != nil {
		log.Fatalf("Build: %v", err)
	}
	if err := app.Run(nil); err != nil {
		log.Fatalf("Run: %v", err)
	}
}
