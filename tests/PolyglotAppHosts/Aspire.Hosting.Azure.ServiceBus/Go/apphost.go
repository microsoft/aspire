package main

import (
	"log"

	"apphost/modules/aspire"
)

func main() {
	builder, err := aspire.CreateBuilder()
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
				emulator.WithHostPort(aspire.Float64Ptr(5672))
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
		CorrelationId: aspire.StringPtr("order-123"),
		Subject:       aspire.StringPtr("OrderCreated"),
		ContentType:   aspire.StringPtr("application/json"),
		MessageId:     aspire.StringPtr("msg-001"),
		ReplyTo:       aspire.StringPtr("reply-queue"),
		SessionId:     aspire.StringPtr("session-1"),
		SendTo:        aspire.StringPtr("destination"),
	}
	_ = &aspire.AzureServiceBusRule{
		Name:              "order-filter",
		FilterType:        aspire.AzureServiceBusFilterTypeCorrelationFilter,
		CorrelationFilter: filter,
	}

	// ── 6. WithProperties — callbacks on Queue, Topic, Subscription ────────────
	queue.WithProperties(func(q aspire.AzureServiceBusQueueResource) {
		// Set all queue properties
		q.SetDeadLetteringOnMessageExpiration(aspire.BoolPtr(true))
		q.SetDefaultMessageTimeToLive(aspire.Float64Ptr(36000000000))           // 1 hour in ticks
		q.SetDuplicateDetectionHistoryTimeWindow(aspire.Float64Ptr(6000000000)) // 10 min in ticks
		q.SetForwardDeadLetteredMessagesTo(aspire.StringPtr("dead-letter-queue"))
		q.SetForwardTo(aspire.StringPtr("forwarding-queue"))
		q.SetLockDuration(aspire.Float64Ptr(300000000)) // 30 seconds in ticks
		q.SetMaxDeliveryCount(aspire.Float64Ptr(10))
		q.SetRequiresDuplicateDetection(aspire.BoolPtr(true))
		q.SetRequiresSession(aspire.BoolPtr(false))

		// Read back properties to verify getter generation
		_, _ = q.DeadLetteringOnMessageExpiration()
		_, _ = q.DefaultMessageTimeToLive()
		_, _ = q.ForwardTo()
		_, _ = q.MaxDeliveryCount()
	})

	topic.WithProperties(func(t aspire.AzureServiceBusTopicResource) {
		t.SetDefaultMessageTimeToLive(aspire.Float64Ptr(6048000000000))         // 7 days in ticks
		t.SetDuplicateDetectionHistoryTimeWindow(aspire.Float64Ptr(3000000000)) // 5 min in ticks
		t.SetRequiresDuplicateDetection(aspire.BoolPtr(false))

		_, _ = t.RequiresDuplicateDetection()
	})

	subscription.WithProperties(func(s aspire.AzureServiceBusSubscriptionResource) {
		s.SetDeadLetteringOnMessageExpiration(aspire.BoolPtr(true))
		s.SetDefaultMessageTimeToLive(aspire.Float64Ptr(72000000000)) // 2 hours in ticks
		s.SetForwardDeadLetteredMessagesTo(aspire.StringPtr("sub-dlq"))
		s.SetForwardTo(aspire.StringPtr("sub-forward"))
		s.SetLockDuration(aspire.Float64Ptr(600000000)) // 1 min in ticks
		s.SetMaxDeliveryCount(aspire.Float64Ptr(5))
		s.SetRequiresSession(aspire.BoolPtr(false))

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

	// ── 7. WithServiceBusRoleAssignments — enum-based role assignment shim ────
	// On the parent ServiceBus resource (all 3 roles)
	serviceBus.WithServiceBusRoleAssignments(serviceBus, []aspire.AzureServiceBusRole{
		aspire.AzureServiceBusRoleAzureServiceBusDataOwner,
		aspire.AzureServiceBusRoleAzureServiceBusDataSender,
		aspire.AzureServiceBusRoleAzureServiceBusDataReceiver,
	})

	// On child resources
	queue.WithServiceBusRoleAssignments(serviceBus, []aspire.AzureServiceBusRole{
		aspire.AzureServiceBusRoleAzureServiceBusDataReceiver,
	})
	topic.WithServiceBusRoleAssignments(serviceBus, []aspire.AzureServiceBusRole{
		aspire.AzureServiceBusRoleAzureServiceBusDataSender,
	})
	subscription.WithServiceBusRoleAssignments(serviceBus, []aspire.AzureServiceBusRole{
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
	if err := app.Run(); err != nil {
		log.Fatalf(aspire.FormatError(err))
	}
}
