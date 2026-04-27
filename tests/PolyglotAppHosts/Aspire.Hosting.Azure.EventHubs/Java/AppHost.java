import aspire.*;

void main() throws Exception {
        var builder = DistributedApplication.CreateBuilder();
        var eventHubs = builder.addAzureEventHubs("eventhubs");
        eventHubs.withRoleAssignments(eventHubs, new AzureEventHubsRole[] { AzureEventHubsRole.AZURE_EVENT_HUBS_DATA_OWNER });
        var hub = eventHubs.addHub("orders", "orders-hub");
        hub.withProperties((configuredHub) -> {
            configuredHub.setHubName("orders-hub");
            var _hubName = configuredHub.hubName();
            configuredHub.setPartitionCount(2);
            var _partitionCount = configuredHub.partitionCount();
        });
        var consumerGroup = hub.addConsumerGroup("processors", "processor-group");
        consumerGroup.withRoleAssignments(eventHubs, new AzureEventHubsRole[] { AzureEventHubsRole.AZURE_EVENT_HUBS_DATA_RECEIVER });
        eventHubs.runAsEmulator((emulator) -> {
                emulator
                    .withHostPort(5673.0)
                    .withConfigurationFile("./eventhubs.config.json")
                    .withRoleAssignments(eventHubs, new AzureEventHubsRole[] { AzureEventHubsRole.AZURE_EVENT_HUBS_DATA_SENDER });
            });
        builder.build().run();
    }
