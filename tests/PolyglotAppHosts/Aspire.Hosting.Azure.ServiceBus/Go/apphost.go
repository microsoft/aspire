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

	// ── 1. AddAzureServiceBus ──────────────────────────────────────────────────
	serviceBus := builder.AddAzureServiceBus("messaging")
	if serviceBus.Err() != nil {
		log.Fatalf(aspire.FormatError(serviceBus.Err()))
	}

	// ── 2. RunAsEmulator — with ConfigureContainer callback ────────────────────
	emulatorBus := builder.AddAzureServiceBus("messaging-emulator").
		RunAsEmulator(&aspire.RunAsEmulatorOptions{
			ConfigureContainer: func(emulator aspire.AzureServiceBusEmulatorResource) {
				emulator.WithConfigurationFile("./servicebus-config.json")
				emulator.WithHostPort(5672)
			},
		})
	if emulatorBus.Err() != nil {
		log.Fatalf(aspire.FormatError(emulatorBus.Err()))
	}

	// ── 3. AddServiceBusQueue — factory method returns Queue type ──────────────
	queue := serviceBus.AddServiceBusQueue("orders", &aspire.AddServiceBusQueueOptions{
		QueueName: aspire.StringPtr("orders-queue"),
	})
	if queue.Err() != nil {
		log.Fatalf(aspire.FormatError(queue.Err()))
	}

	// ── 4. AddServiceBusTopic — factory method returns Topic type ──────────────
	topic := serviceBus.AddServiceBusTopic("events", &aspire.AddServiceBusTopicOptions{
		TopicName: aspire.StringPtr("events-topic"),
	})
	if topic.Err() != nil {
		log.Fatalf(aspire.FormatError(topic.Err()))
	}

	// ── 5. AddServiceBusSubscription — factory on Topic returns Subscription ───
	subscription := topic.AddServiceBusSubscription("audit", &aspire.AddServiceBusSubscriptionOptions{
		SubscriptionName: aspire.StringPtr("audit-sub"),
	})
	if subscription.Err() != nil {
		log.Fatalf(aspire.FormatError(subscription.Err()))
	}

	_ = queue.Parent()
	_ = queue.ConnectionStringExpression()
	_ = topic.Parent()
	_ = topic.ConnectionStringExpression()
	_ = subscription.Parent()
	_ = subscription.ConnectionStringExpression()

	// ── DTO types ───────────────────────────────────────────────────────────────
	filter := &aspire.AzureServiceBusCorrelationFilter{
		CorrelationId: "order-123",
		Subject:       "OrderCreated",
		ContentType:   "application/json",
		MessageId:     "msg-001",
		ReplyTo:       "reply-queue",
		SessionId:     "session-1",
		SendTo:        "destination",
	}
	_ = &aspire.AzureServiceBusRule{
		Name:              "order-filter",
		FilterType:        aspire.AzureServiceBusFilterTypeCorrelationFilter,
		CorrelationFilter: filter,
	}

	// ── 6. WithProperties — callbacks on Queue, Topic, Subscription ────────────
	queue.WithProperties(func(q aspire.AzureServiceBusQueueResource) {
		// Set all queue properties
		q.SetDeadLetteringOnMessageExpiration(true)
		q.SetDefaultMessageTimeToLive(36000000000)           // 1 hour in ticks
		q.SetDuplicateDetectionHistoryTimeWindow(6000000000) // 10 min in ticks
		q.SetForwardDeadLetteredMessagesTo("dead-letter-queue")
		q.SetForwardTo("forwarding-queue")
		q.SetLockDuration(300000000) // 30 seconds in ticks
		q.SetMaxDeliveryCount(10)
		q.SetRequiresDuplicateDetection(true)
		q.SetRequiresSession(false)

		// Read back properties to verify getter generation
		_, _ = q.DeadLetteringOnMessageExpiration()
		_, _ = q.DefaultMessageTimeToLive()
		_, _ = q.ForwardTo()
		_, _ = q.MaxDeliveryCount()
	})

	topic.WithProperties(func(t aspire.AzureServiceBusTopicResource) {
		t.SetDefaultMessageTimeToLive(6048000000000)         // 7 days in ticks
		t.SetDuplicateDetectionHistoryTimeWindow(3000000000) // 5 min in ticks
		t.SetRequiresDuplicateDetection(false)

		_, _ = t.RequiresDuplicateDetection()
	})

	subscription.WithProperties(func(s aspire.AzureServiceBusSubscriptionResource) {
		s.SetDeadLetteringOnMessageExpiration(true)
		s.SetDefaultMessageTimeToLive(72000000000) // 2 hours in ticks
		s.SetForwardDeadLetteredMessagesTo("sub-dlq")
		s.SetForwardTo("sub-forward")
		s.SetLockDuration(600000000) // 1 min in ticks
		s.SetMaxDeliveryCount(5)
		s.SetRequiresSession(false)

		// Read back a subscription property
		_, _ = s.LockDuration()

		// Add rules using List.Add() and the DTO types
		_ = s.Rules().Add(&aspire.AzureServiceBusRule{
			Name:              "order-filter",
			FilterType:        aspire.AzureServiceBusFilterTypeCorrelationFilter,
			CorrelationFilter: filter,
		})
		_ = s.Rules().Add(&aspire.AzureServiceBusRule{
			Name:       "sql-filter",
			FilterType: aspire.AzureServiceBusFilterTypeSqlFilter,
		})
	})

	_ = aspire.AzureServiceBusFilterTypeSqlFilter
	_ = aspire.AzureServiceBusFilterTypeCorrelationFilter

	// ── 7. WithRoleAssignments — enum-based role assignment shim ───────────────
	// On the parent ServiceBus resource (all 3 roles)
	serviceBus.WithRoleAssignments(serviceBus, []aspire.AzureServiceBusRole{
		aspire.AzureServiceBusRoleAzureServiceBusDataOwner,
		aspire.AzureServiceBusRoleAzureServiceBusDataSender,
		aspire.AzureServiceBusRoleAzureServiceBusDataReceiver,
	})

	// On child resources
	queue.WithRoleAssignments(serviceBus, []aspire.AzureServiceBusRole{
		aspire.AzureServiceBusRoleAzureServiceBusDataReceiver,
	})
	topic.WithRoleAssignments(serviceBus, []aspire.AzureServiceBusRole{
		aspire.AzureServiceBusRoleAzureServiceBusDataSender,
	})
	subscription.WithRoleAssignments(serviceBus, []aspire.AzureServiceBusRole{
		aspire.AzureServiceBusRoleAzureServiceBusDataReceiver,
	})

	// ── 8. Fluent chaining — verify correct return types enable chaining ───────
	// Queue: factory returns QueueResource, can chain withProperties
	serviceBus.AddServiceBusQueue("chained-queue").
		WithProperties(func(_ aspire.AzureServiceBusQueueResource) {})

	// Topic → Subscription chaining
	serviceBus.AddServiceBusTopic("chained-topic").
		AddServiceBusSubscription("chained-sub").
		WithProperties(func(_ aspire.AzureServiceBusSubscriptionResource) {})

	if serviceBus.Err() != nil {
		log.Fatalf(aspire.FormatError(serviceBus.Err()))
	}
	if emulatorBus.Err() != nil {
		log.Fatalf(aspire.FormatError(emulatorBus.Err()))
	}
	if queue.Err() != nil {
		log.Fatalf(aspire.FormatError(queue.Err()))
	}
	if topic.Err() != nil {
		log.Fatalf(aspire.FormatError(topic.Err()))
	}
	if subscription.Err() != nil {
		log.Fatalf(aspire.FormatError(subscription.Err()))
	}

	app, err := builder.Build()
	if err != nil {
		log.Fatalf(aspire.FormatError(err))
	}
	if err := app.Run(nil); err != nil {
		log.Fatalf(aspire.FormatError(err))
	}
}
