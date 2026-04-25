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

	kafka := builder.AddKafka("broker")

	kafkaWithUI := kafka.WithKafkaUI(&aspire.WithKafkaUIOptions{
		ContainerName: aspire.StringPtr("my-kafka-ui"),
		ConfigureContainer: func(ui aspire.KafkaUIContainerResource) {
			ui.WithHostPort(9000)
		},
	})
	kafkaWithUI.WithDataVolume()

	_ = kafka.PrimaryEndpoint()
	_ = kafka.Host()
	_ = kafka.Port()
	_ = kafka.InternalEndpoint()
	_ = kafka.ConnectionStringExpression()
	if err = kafka.Err(); err != nil {
		log.Fatalf(aspire.FormatError(err))
	}

	kafka2 := builder.AddKafka("broker2", &aspire.AddKafkaOptions{Port: aspire.Float64Ptr(19092)})
	kafka2.WithDataBindMount("/tmp/kafka-data")
	if err = kafka2.Err(); err != nil {
		log.Fatalf(aspire.FormatError(err))
	}

	app, err := builder.Build()
	if err != nil {
		log.Fatalf(aspire.FormatError(err))
	}
	if err := app.Run(nil); err != nil {
		log.Fatalf(aspire.FormatError(err))
	}
}
