import { AzureEventHubsRole, createBuilder } from './.modules/aspire.js';

const builder = await createBuilder();
const eventHubs = await builder.addAzureEventHubs('eventhubs');

await eventHubs.withEventHubsRoleAssignments(eventHubs, [AzureEventHubsRole.AzureEventHubsDataOwner]);

const hub = await eventHubs.addHub('orders', { hubName: 'orders-hub' });
await hub.withProperties(async (configuredHub) => {
    await configuredHub.hubName.set('orders-hub');
    const _hubName: string = await configuredHub.hubName.get();

    await configuredHub.partitionCount.set(2);
    const _partitionCount: number | undefined = await configuredHub.partitionCount.get();
});

const _hubParent = await hub.parent.get();
const _hubConnectionString = await hub.connectionStringExpression.get();

const consumerGroup = await hub.addConsumerGroup('processors', { groupName: 'processor-group' });
await consumerGroup.withEventHubsRoleAssignments(eventHubs, [AzureEventHubsRole.AzureEventHubsDataReceiver]);

await eventHubs.runAsEmulator({
    configureContainer: async (emulator) => {
        await emulator
            .withHostPort({ port: 5673 })
            .withConfigurationFile('./eventhubs.config.json')
            .withEventHubsRoleAssignments(eventHubs, [AzureEventHubsRole.AzureEventHubsDataSender]);
    }
});

await builder.build().run();
