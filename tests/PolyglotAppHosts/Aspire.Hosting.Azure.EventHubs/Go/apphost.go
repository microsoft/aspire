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

	// ── 1. addAzureEventHubs ─────────────────────────────────────────────────
	eventHubs := builder.AddAzureEventHubs("eventhubs")

	// ── 2. withEventHubsRoleAssignments ──────────────────────────────────────
	_ = eventHubs.WithEventHubsRoleAssignments(eventHubs, []aspire.AzureEventHubsRole{
		aspire.AzureEventHubsRoleAzureEventHubsDataOwner,
	})

	// ── 3. addHub (simple) + addHubWithOpts ───────────────────────────────────
	_ = eventHubs.AddHub("orders-simple")
	hub := eventHubs.AddHubWithOpts("orders", &aspire.AddHubOptions{
		HubName: aspire.StringPtr("orders-hub"),
	})

	// ── 4. withProperties (typed callback) ────────────────────────────────────
	hub.WithProperties(func(configuredHub *aspire.AzureEventHubResource) {
		configuredHub.SetHubName("orders-hub")
		_, _ = configuredHub.HubName()
		configuredHub.SetPartitionCount(2)
		_, _ = configuredHub.PartitionCount()
	})

	// ── 5. property getters ───────────────────────────────────────────────────
	_, _ = hub.Parent()
	_, _ = hub.ConnectionStringExpression()

	// ── 6. addConsumerGroup (simple) + addConsumerGroupWithOpts ───────────────
	_ = hub.AddConsumerGroup("processors-simple")
	consumerGroup := hub.AddConsumerGroupWithOpts("processors", &aspire.AddConsumerGroupOptions{
		GroupName: aspire.StringPtr("processor-group"),
	})
	_ = consumerGroup.WithEventHubsRoleAssignments(eventHubs, []aspire.AzureEventHubsRole{
		aspire.AzureEventHubsRoleAzureEventHubsDataReceiver,
	})

	// ── 7. runAsEmulator (typed callback) ─────────────────────────────────────
	eventHubs.RunAsEmulator(func(emulator *aspire.AzureEventHubsEmulatorResource) {
		emulator.
			WithHostPort(5673).
			WithConfigurationFile("./eventhubs.config.json").
			WithEventHubsRoleAssignments(eventHubs, []aspire.AzureEventHubsRole{
				aspire.AzureEventHubsRoleAzureEventHubsDataSender,
			})
	})

	app, err := builder.Build()
	if err != nil {
		log.Fatalf("Build: %v", err)
	}
	if err := app.Run(nil); err != nil {
		log.Fatalf("Run: %v", err)
	}
}
