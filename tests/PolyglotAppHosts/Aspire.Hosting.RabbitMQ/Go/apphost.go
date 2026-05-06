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

	rabbitmq := builder.AddRabbitMQ("messaging")
	rabbitmq.WithDataVolume()
	rabbitmq.WithManagementPlugin()
	if err = rabbitmq.Err(); err != nil {
		log.Fatalf(aspire.FormatError(err))
	}

	rabbitmq2 := builder.AddRabbitMQ("messaging2")
	rabbitmq2.WithLifetime(aspire.ContainerLifetimePersistent)
	rabbitmq2.WithDataVolume()
	rabbitmq2.WithManagementPlugin(&aspire.WithManagementPluginOptions{Port: aspire.Float64Ptr(15673)})
	if err = rabbitmq2.Err(); err != nil {
		log.Fatalf(aspire.FormatError(err))
	}

	_ = rabbitmq.PrimaryEndpoint()
	_ = rabbitmq.ManagementEndpoint()
	_ = rabbitmq.Host()
	_ = rabbitmq.Port()
	_ = rabbitmq.UriExpression()
	_ = rabbitmq.UserNameReference()
	_ = rabbitmq.ConnectionStringExpression()

	app, err := builder.Build()
	if err != nil {
		log.Fatalf(aspire.FormatError(err))
	}
	if err := app.Run(nil); err != nil {
		log.Fatalf(aspire.FormatError(err))
	}
}
