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

	// ---- AddRabbitMQ ----
	rabbitmq := builder.AddRabbitMQ("messaging")
	rabbitmq.WithDataVolume()
	rabbitmq.WithManagementPlugin()
	if err = rabbitmq.Err(); err != nil {
		log.Fatalf("rabbitmq: %v", err)
	}

	// ---- Fluent chaining with Lifetime, DataVolume, ManagementPluginWithPort ----
	rabbitmq2 := builder.AddRabbitMQ("messaging2")
	rabbitmq2.WithLifetime(aspire.ContainerLifetimePersistent)
	rabbitmq2.WithDataVolume()
	rabbitmq2.WithManagementPluginWithPort(15673)
	if err = rabbitmq2.Err(); err != nil {
		log.Fatalf("rabbitmq2: %v", err)
	}

	// ---- Property access on RabbitMQServerResource ----
	_, _ = rabbitmq.PrimaryEndpoint()
	_, _ = rabbitmq.ManagementEndpoint()
	_, _ = rabbitmq.Host()
	_, _ = rabbitmq.Port()
	_, _ = rabbitmq.UriExpression()
	_, _ = rabbitmq.UserNameReference()
	_, _ = rabbitmq.ConnectionStringExpression()

	app, err := builder.Build()
	if err != nil {
		log.Fatalf("Build: %v", err)
	}
	if err := app.Run(nil); err != nil {
		log.Fatalf("Run: %v", err)
	}
}
