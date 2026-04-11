// Aspire TypeScript AppHost — Azure Service Bus validation
// Exercises every exported member of Aspire.Hosting.Azure.ServiceBus

import {
    createBuilder,
    AzureServiceBusRole,
    AzureServiceBusFilterType,
    type AzureServiceBusRule,
    type AzureServiceBusCorrelationFilter,
} from './.modules/aspire.js';

const builder = await createBuilder();

// ── 1. addAzureServiceBus ──────────────────────────────────────────────────
const serviceBus = await builder.addAzureServiceBus("messaging");

// ── 2. runAsEmulator — with configureContainer callback ────────────────────
const emulatorBus = await builder
    .addAzureServiceBus("messaging-emulator")
    .runAsEmulator({
        configureContainer: async (emulator) => {
            await emulator.withConfigurationFile("./servicebus-config.json");
            await emulator.withHostPort({ port: 5672 });
        },
    });

// ── 3. addServiceBusQueue — factory method returns Queue type ──────────────
const queue = await serviceBus.addServiceBusQueue("orders", {
    queueName: "orders-queue",
});

// ── 4. addServiceBusTopic — factory method returns Topic type ──────────────
const topic = await serviceBus.addServiceBusTopic("events", {
    topicName: "events-topic",
});

// ── 5. addServiceBusSubscription — factory on Topic returns Subscription ───
const subscription = await topic.addServiceBusSubscription("audit", {
    subscriptionName: "audit-sub",
});

const _queueParent = await queue.parent.get();
const _queueConnectionString = await queue.connectionStringExpression.get();
const _topicParent = await topic.parent.get();
const _topicConnectionString = await topic.connectionStringExpression.get();
const _subscriptionParent = await subscription.parent.get();
const _subscriptionConnectionString = await subscription.connectionStringExpression.get();

// ── DTO types ───────────────────────────────────────────────────────────────
const filter: AzureServiceBusCorrelationFilter = {
    correlationId: "order-123",
    subject: "OrderCreated",
    contentType: "application/json",
    messageId: "msg-001",
    replyTo: "reply-queue",
    sessionId: "session-1",
    sendTo: "destination",
};

const rule: AzureServiceBusRule = {
    name: "order-filter",
    filterType: AzureServiceBusFilterType.CorrelationFilter,
    correlationFilter: filter,
};

// ── 6. withProperties — callbacks on Queue, Topic, Subscription ────────────
// TimeSpan properties map to number (ticks) in TypeScript
await queue.withProperties(async (q) => {
    // Set all queue properties
    await q.deadLetteringOnMessageExpiration.set(true);
    await q.defaultMessageTimeToLive.set(36000000000);  // 1 hour in ticks
    await q.duplicateDetectionHistoryTimeWindow.set(6000000000);  // 10 min in ticks
    await q.forwardDeadLetteredMessagesTo.set("dead-letter-queue");
    await q.forwardTo.set("forwarding-queue");
    await q.lockDuration.set(300000000);  // 30 seconds in ticks
    await q.maxDeliveryCount.set(10);
    await q.requiresDuplicateDetection.set(true);
    await q.requiresSession.set(false);

    // Read back properties to verify getter generation
    const _dlq: boolean = await q.deadLetteringOnMessageExpiration.get();
    const _ttl: number = await q.defaultMessageTimeToLive.get();
    const _fwd: string = await q.forwardTo.get();
    const _maxDel: number = await q.maxDeliveryCount.get();
});

await topic.withProperties(async (t) => {
    await t.defaultMessageTimeToLive.set(6048000000000);  // 7 days in ticks
    await t.duplicateDetectionHistoryTimeWindow.set(3000000000);  // 5 min in ticks
    await t.requiresDuplicateDetection.set(false);

    const _dupDetect: boolean = await t.requiresDuplicateDetection.get();
});

await subscription.withProperties(async (s) => {
    await s.deadLetteringOnMessageExpiration.set(true);
    await s.defaultMessageTimeToLive.set(72000000000);  // 2 hours in ticks
    await s.forwardDeadLetteredMessagesTo.set("sub-dlq");
    await s.forwardTo.set("sub-forward");
    await s.lockDuration.set(600000000);  // 1 min in ticks
    await s.maxDeliveryCount.set(5);
    await s.requiresSession.set(false);

    // Read back a subscription property
    const _lock: number = await s.lockDuration.get();

    // Add rules using AspireList.add() and the DTO types
    await s.rules.add({
        name: "order-filter",
        filterType: AzureServiceBusFilterType.CorrelationFilter,
        correlationFilter: filter,
    });

    await s.rules.add({
        name: "sql-filter",
        filterType: AzureServiceBusFilterType.SqlFilter,
    });
});

// ── 7. withRoleAssignments — enum-based role assignment shim ───────────────
// On the parent ServiceBus resource (all 3 roles)
await serviceBus.withServiceBusRoleAssignments(serviceBus, [
    AzureServiceBusRole.AzureServiceBusDataOwner,
    AzureServiceBusRole.AzureServiceBusDataSender,
    AzureServiceBusRole.AzureServiceBusDataReceiver,
]);

// On child resources
await queue.withServiceBusRoleAssignments(serviceBus, [AzureServiceBusRole.AzureServiceBusDataReceiver]);
await topic.withServiceBusRoleAssignments(serviceBus, [AzureServiceBusRole.AzureServiceBusDataSender]);
await subscription.withServiceBusRoleAssignments(serviceBus, [AzureServiceBusRole.AzureServiceBusDataReceiver]);

// ── 8. Verify enum values are accessible ────────────────────────────────────
const _sqlFilter = AzureServiceBusFilterType.SqlFilter;
const _correlationFilter = AzureServiceBusFilterType.CorrelationFilter;

// ── 9. Fluent chaining — verify correct return types enable chaining ───────
// Queue: factory returns QueueResource, can chain withProperties
await serviceBus
    .addServiceBusQueue("chained-queue")
    .withProperties(async (_q) => {});

// Topic → Subscription chaining
await serviceBus
    .addServiceBusTopic("chained-topic")
    .addServiceBusSubscription("chained-sub")
    .withProperties(async (_s) => {});

await builder.build().run();
