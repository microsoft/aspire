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

	serviceBus := builder.AddAzureServiceBus("resource")

	queue := serviceBus.AddServiceBusQueue("resource", nil)

	topic := serviceBus.AddServiceBusTopic("resource", nil)

	subscription := topic.AddServiceBusSubscription("resource", nil)

	queue.WithProperties(nil)
	topic.WithProperties(nil)
	subscription.WithProperties(nil)

	serviceBus.WithServiceBusRoleAssignments(serviceBus, nil)
	queue.WithServiceBusRoleAssignments(serviceBus, nil)
	topic.WithServiceBusRoleAssignments(serviceBus, nil)
	subscription.WithServiceBusRoleAssignments(serviceBus, nil)
	if err = serviceBus.Err(); err != nil {
		log.Fatalf("serviceBus: %v", err)
	}
	if err = queue.Err(); err != nil {
		log.Fatalf("queue: %v", err)
	}
	if err = topic.Err(); err != nil {
		log.Fatalf("topic: %v", err)
	}
	if err = subscription.Err(); err != nil {
		log.Fatalf("subscription: %v", err)
	}

	app, err := builder.Build()
	if err != nil {
		log.Fatalf("Build: %v", err)
	}
	if err := app.Run(nil); err != nil {
		log.Fatalf("Run: %v", err)
	}
}
