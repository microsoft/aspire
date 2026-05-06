package main

import (
	"log"

	"apphost/modules/aspire"
)

func main() {
	builder, err := aspire.CreateBuilder(nil)
	if err != nil {
		log.Fatalf(aspire.FormatError(err))
	}

	eventHubs := builder.AddAzureEventHubs("eventhubs")
	if eventHubs.Err() != nil {
		log.Fatalf(aspire.FormatError(eventHubs.Err()))
	}

	eventHubs.WithRoleAssignments(eventHubs, []aspire.AzureEventHubsRole{
		aspire.AzureEventHubsRoleAzureEventHubsDataOwner,
	})

	hub := eventHubs.AddHub("orders", &aspire.AddHubOptions{
		HubName: aspire.StringPtr("orders-hub"),
	}).WithProperties(func(configuredHub aspire.AzureEventHubResource) {
		configuredHub.SetHubName("orders-hub")
		_, _ = configuredHub.HubName()
		configuredHub.SetPartitionCount(2)
		_, _ = configuredHub.PartitionCount()
	})
	if hub.Err() != nil {
		log.Fatalf(aspire.FormatError(hub.Err()))
	}

	_ = hub.Parent()
	_ = hub.ConnectionStringExpression()

	consumerGroup := hub.AddConsumerGroup("processors", &aspire.AddConsumerGroupOptions{
		GroupName: aspire.StringPtr("processor-group"),
	}).WithRoleAssignments(eventHubs, []aspire.AzureEventHubsRole{
		aspire.AzureEventHubsRoleAzureEventHubsDataReceiver,
	})
	if consumerGroup.Err() != nil {
		log.Fatalf(aspire.FormatError(consumerGroup.Err()))
	}

	eventHubs.RunAsEmulator(&aspire.RunAsEmulatorOptions{
		ConfigureContainer: func(emulator aspire.AzureEventHubsEmulatorResource) {
			emulator.
				WithHostPort(5673).
				WithConfigurationFile("./eventhubs.config.json").
				WithRoleAssignments(eventHubs, []aspire.AzureEventHubsRole{
					aspire.AzureEventHubsRoleAzureEventHubsDataSender,
				})
		},
	})

	app, err := builder.Build()
	if err != nil {
		log.Fatalf(aspire.FormatError(err))
	}
	if err := app.Run(nil); err != nil {
		log.Fatalf(aspire.FormatError(err))
	}
}
