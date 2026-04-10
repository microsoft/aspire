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
	_, _ = eventHubs.WithEventHubsRoleAssignments()

	hub, err := eventHubs.AddHub("resource")
	if err != nil {
		log.Fatalf("AddHub: %v", err)
	}
	_, _ = hub.WithProperties()

	consumerGroup, err := hub.AddConsumerGroup("resource")
	if err != nil {
		log.Fatalf("AddConsumerGroup: %v", err)
	}
	_, _ = consumerGroup.WithEventHubsRoleAssignments()

	_, _ = eventHubs.RunAsEmulator()

	app, err := builder.Build()
	if err != nil {
		log.Fatalf("Build: %v", err)
	}
	if err := app.Run(nil); err != nil {
		log.Fatalf("Run: %v", err)
	}
}
