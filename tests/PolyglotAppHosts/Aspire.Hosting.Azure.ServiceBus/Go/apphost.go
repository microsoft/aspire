// Aspire Go validation AppHost - Aspire.Hosting.Azure.ServiceBus
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

	serviceBus, err := builder.AddAzureServiceBus("resource")
	if err != nil {
		log.Fatalf("AddAzureServiceBus: %v", err)
	}

	queue, err := serviceBus.AddServiceBusQueue("resource", nil)
	if err != nil {
		log.Fatalf("AddServiceBusQueue: %v", err)
	}

	topic, err := serviceBus.AddServiceBusTopic("resource", nil)
	if err != nil {
		log.Fatalf("AddServiceBusTopic: %v", err)
	}

	subscription, err := topic.AddServiceBusSubscription("resource", nil)
	if err != nil {
		log.Fatalf("AddServiceBusSubscription: %v", err)
	}

	queue.WithProperties(nil)
	topic.WithProperties(nil)
	subscription.WithProperties(nil)

	_, _ = serviceBus.WithServiceBusRoleAssignments(serviceBus, nil)
	_, _ = queue.WithServiceBusRoleAssignments(serviceBus, nil)
	_, _ = topic.WithServiceBusRoleAssignments(serviceBus, nil)
	_, _ = subscription.WithServiceBusRoleAssignments(serviceBus, nil)

	app, err := builder.Build()
	if err != nil {
		log.Fatalf("Build: %v", err)
	}
	if err := app.Run(nil); err != nil {
		log.Fatalf("Run: %v", err)
	}
}
