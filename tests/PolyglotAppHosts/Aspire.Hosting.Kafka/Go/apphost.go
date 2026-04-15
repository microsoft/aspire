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

	// addKafka — factory method with no options
	kafka := builder.AddKafka("broker")

	// withKafkaUI — adds Kafka UI management container with callback and container name
	kafkaWithUI := kafka.WithKafkaUIWithOpts(
		&aspire.WithKafkaUIOptions{ContainerName: aspire.StringPtr("my-kafka-ui")},
		func(ui *aspire.KafkaUIContainerResource) {
			ui.WithHostPort(9000)
		},
	)

	// withDataVolume — adds a data volume
	kafkaWithUI.WithDataVolume()

	_, _ = kafka.PrimaryEndpoint()
	_, _ = kafka.Host()
	_, _ = kafka.Port()
	_, _ = kafka.InternalEndpoint()
	_, _ = kafka.ConnectionStringExpression()
	if err = kafka.Err(); err != nil {
		log.Fatalf("kafka: %v", err)
	}

	// withDataBindMount — adds a data bind mount
	kafka2 := builder.AddKafkaWithOpts("broker2", &aspire.AddKafkaOptions{Port: aspire.Float64Ptr(19092)})
	kafka2.WithDataBindMount("/tmp/kafka-data")
	if err = kafka2.Err(); err != nil {
		log.Fatalf("kafka2: %v", err)
	}

	app, err := builder.Build()
	if err != nil {
		log.Fatalf("Build: %v", err)
	}
	if err := app.Run(nil); err != nil {
		log.Fatalf("Run: %v", err)
	}
}
