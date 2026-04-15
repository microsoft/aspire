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

	// ── 1. addAzureServiceBus ─────────────────────────────────────────────────
	serviceBus := builder.AddAzureServiceBus("messaging")

	// ── 2. runAsEmulator with configureContainer callback ─────────────────────
	emulatorBus := builder.AddAzureServiceBus("messaging-emulator")
	emulatorBus.RunAsEmulator(func(emulator *aspire.AzureServiceBusEmulatorResource) {
		emulator.WithConfigurationFile("./servicebus-config.json")
		emulator.WithHostPort(5672)
	})

	// ── 3. addServiceBusQueue with queueName option ───────────────────────────
	queue := serviceBus.AddServiceBusQueueWithOpts("orders",
		&aspire.AddServiceBusQueueOptions{QueueName: aspire.StringPtr("orders-queue")})

	// ── 4. addServiceBusTopic with topicName option ───────────────────────────
	topic := serviceBus.AddServiceBusTopicWithOpts("events",
		&aspire.AddServiceBusTopicOptions{TopicName: aspire.StringPtr("events-topic")})

	// ── 5. addServiceBusSubscription with subscriptionName option ─────────────
	subscription := topic.AddServiceBusSubscriptionWithOpts("audit",
		&aspire.AddServiceBusSubscriptionOptions{SubscriptionName: aspire.StringPtr("audit-sub")})

	// ── 6. property accessors ─────────────────────────────────────────────────
	_, _ = queue.Parent()
	_, _ = queue.ConnectionStringExpression()
	_, _ = topic.Parent()
	_, _ = topic.ConnectionStringExpression()
	_, _ = subscription.Parent()
	_, _ = subscription.ConnectionStringExpression()

	// ── 7. withProperties callbacks — queue ───────────────────────────────────
	queue.WithProperties(func(q *aspire.AzureServiceBusQueueResource) {
		q.SetDeadLetteringOnMessageExpiration(true)
		q.SetDefaultMessageTimeToLive(36000000000)
		q.SetDuplicateDetectionHistoryTimeWindow(6000000000)
		q.SetForwardDeadLetteredMessagesTo("dead-letter-queue")
		q.SetForwardTo("forwarding-queue")
		q.SetLockDuration(300000000)
		q.SetMaxDeliveryCount(10)
		q.SetRequiresDuplicateDetection(true)
		q.SetRequiresSession(false)

		_, _ = q.DeadLetteringOnMessageExpiration()
		_, _ = q.DefaultMessageTimeToLive()
		_, _ = q.ForwardTo()
		_, _ = q.MaxDeliveryCount()
	})

	// ── 8. withProperties callbacks — topic ───────────────────────────────────
	topic.WithProperties(func(t *aspire.AzureServiceBusTopicResource) {
		t.SetDefaultMessageTimeToLive(6048000000000)
		t.SetDuplicateDetectionHistoryTimeWindow(3000000000)
		t.SetRequiresDuplicateDetection(false)

		_, _ = t.RequiresDuplicateDetection()
	})

	// ── 9. withProperties callbacks — subscription ────────────────────────────
	subscription.WithProperties(func(s *aspire.AzureServiceBusSubscriptionResource) {
		s.SetDeadLetteringOnMessageExpiration(true)
		s.SetDefaultMessageTimeToLive(72000000000)
		s.SetForwardDeadLetteredMessagesTo("sub-dlq")
		s.SetForwardTo("sub-forward")
		s.SetLockDuration(600000000)
		s.SetMaxDeliveryCount(5)
		s.SetRequiresSession(false)

		_, _ = s.LockDuration()

		// Access rules list to validate API surface
		_ = s.Rules()
	})

	// ── 10. withServiceBusRoleAssignments ─────────────────────────────────────
	serviceBus.WithServiceBusRoleAssignments(serviceBus, []aspire.AzureServiceBusRole{
		aspire.AzureServiceBusRoleAzureServiceBusDataOwner,
		aspire.AzureServiceBusRoleAzureServiceBusDataSender,
		aspire.AzureServiceBusRoleAzureServiceBusDataReceiver,
	})
	queue.WithServiceBusRoleAssignments(serviceBus, []aspire.AzureServiceBusRole{
		aspire.AzureServiceBusRoleAzureServiceBusDataReceiver,
	})
	topic.WithServiceBusRoleAssignments(serviceBus, []aspire.AzureServiceBusRole{
		aspire.AzureServiceBusRoleAzureServiceBusDataSender,
	})
	subscription.WithServiceBusRoleAssignments(serviceBus, []aspire.AzureServiceBusRole{
		aspire.AzureServiceBusRoleAzureServiceBusDataReceiver,
	})

	// ── 11. fluent chaining ───────────────────────────────────────────────────
	serviceBus.AddServiceBusQueue("chained-queue").
		WithProperties(func(_ *aspire.AzureServiceBusQueueResource) {})

	serviceBus.AddServiceBusTopic("chained-topic").
		AddServiceBusSubscription("chained-sub").
		WithProperties(func(_ *aspire.AzureServiceBusSubscriptionResource) {})

	if err = serviceBus.Err(); err != nil {
		log.Fatalf("serviceBus: %v", err)
	}
	if err = emulatorBus.Err(); err != nil {
		log.Fatalf("emulatorBus: %v", err)
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
