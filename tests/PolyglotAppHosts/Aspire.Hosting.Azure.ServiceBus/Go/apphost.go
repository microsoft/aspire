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

	queue, err := serviceBus.AddServiceBusQueue("resource")
	if err != nil {
		log.Fatalf("AddServiceBusQueue: %v", err)
	}

	topic, err := serviceBus.AddServiceBusTopic("resource")
	if err != nil {
		log.Fatalf("AddServiceBusTopic: %v", err)
	}

	subscription, err := topic.AddServiceBusSubscription("resource")
	if err != nil {
		log.Fatalf("AddServiceBusSubscription: %v", err)
	}

	_, _ = queue.WithProperties()
	_, _ = topic.WithProperties()
	_, _ = subscription.WithProperties()

	_, _ = serviceBus.WithServiceBusRoleAssignments()
	_, _ = queue.WithServiceBusRoleAssignments()
	_, _ = topic.WithServiceBusRoleAssignments()
	_, _ = subscription.WithServiceBusRoleAssignments()

	app, err := builder.Build()
	if err != nil {
		log.Fatalf("Build: %v", err)
	}
	if err := app.Run(nil); err != nil {
		log.Fatalf("Run: %v", err)
	}
}
