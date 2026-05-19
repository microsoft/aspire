import aspire.*;

void main() throws Exception {
        var builder = DistributedApplication.CreateBuilder();
        var serviceBus = builder.addAzureServiceBus("messaging");
        var emulatorBus = builder
    .addAzureServiceBus("messaging-emulator")
    .runAsEmulator((emulator) -> { emulator.withConfigurationFile("./servicebus-config.json"); emulator.withHostPort(5672.0); });
        var queue = serviceBus.addServiceBusQueue("orders", "orders-queue");
        var topic = serviceBus.addServiceBusTopic("events", "events-topic");
        var subscription = topic.addServiceBusSubscription("audit", "audit-sub");
        var filter = new AzureServiceBusCorrelationFilter();
        filter.setCorrelationId("order-123");
        filter.setSubject("OrderCreated");
        filter.setContentType("application/json");
        filter.setMessageId("msg-001");
        filter.setReplyTo("reply-queue");
        filter.setSessionId("session-1");
        filter.setSendTo("destination");

        var rule = new AzureServiceBusRule();
        rule.setName("order-filter");
        rule.setFilterType(AzureServiceBusFilterType.CORRELATION_FILTER);
        rule.setCorrelationFilter(filter);
        queue.withProperties((q) -> { q.setDeadLetteringOnMessageExpiration(true); q.setDefaultMessageTimeToLive(36000000000.0); q.setDuplicateDetectionHistoryTimeWindow(6000000000.0); q.setForwardDeadLetteredMessagesTo("dead-letter-queue"); q.setForwardTo("forwarding-queue"); q.setLockDuration(300000000.0); q.setMaxDeliveryCount(10); q.setRequiresDuplicateDetection(true); q.setRequiresSession(false); var _dlq = q.deadLetteringOnMessageExpiration(); var _ttl = q.defaultMessageTimeToLive(); var _fwd = q.forwardTo(); var _maxDel = q.maxDeliveryCount(); });
        topic.withProperties((t) -> { t.setDefaultMessageTimeToLive(6048000000000.0); t.setDuplicateDetectionHistoryTimeWindow(3000000000.0); t.setRequiresDuplicateDetection(false); var _dupDetect = t.requiresDuplicateDetection(); });
        subscription.withProperties((s) -> { s.setDeadLetteringOnMessageExpiration(true); s.setDefaultMessageTimeToLive(72000000000.0); s.setForwardDeadLetteredMessagesTo("sub-dlq"); s.setForwardTo("sub-forward"); s.setLockDuration(600000000.0); s.setMaxDeliveryCount(5); s.setRequiresSession(false); var _lock = s.lockDuration(); var _rules = s.rules(); });
        serviceBus.withServiceBusRoleAssignments(serviceBus, new AzureServiceBusRole[] { AzureServiceBusRole.AZURE_SERVICE_BUS_DATA_OWNER, AzureServiceBusRole.AZURE_SERVICE_BUS_DATA_SENDER, AzureServiceBusRole.AZURE_SERVICE_BUS_DATA_RECEIVER });
        queue.withServiceBusRoleAssignments(serviceBus, new AzureServiceBusRole[] { AzureServiceBusRole.AZURE_SERVICE_BUS_DATA_RECEIVER });
        topic.withServiceBusRoleAssignments(serviceBus, new AzureServiceBusRole[] { AzureServiceBusRole.AZURE_SERVICE_BUS_DATA_SENDER });
        subscription.withServiceBusRoleAssignments(serviceBus, new AzureServiceBusRole[] { AzureServiceBusRole.AZURE_SERVICE_BUS_DATA_RECEIVER });
        var _sqlFilter = AzureServiceBusFilterType.SQL_FILTER;
        var _correlationFilter = AzureServiceBusFilterType.CORRELATION_FILTER;
        serviceBus
    .addServiceBusQueue("chained-queue")
    .withProperties((_q) -> {  });
        serviceBus
    .addServiceBusTopic("chained-topic")
    .addServiceBusSubscription("chained-sub")
    .withProperties((_s) -> {  });
        builder.build().run();
    }
