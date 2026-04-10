// Aspire Go validation AppHost - Aspire.Hosting.Kafka
// Mirrors the TypeScript/Python/Java fixture for API surface validation.
// Run `aspire restore --apphost apphost.go` to generate the SDK, then `go build ./...`.
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

	kafka := builder.AddKafka("resource", nil)

	kafkaWithUI := kafka.WithKafkaUI(nil, nil)
	kafkaWithUI.WithDataVolume(nil, nil)

	_, _ = kafka.PrimaryEndpoint()
	_, _ = kafka.Host()
	_, _ = kafka.Port()
	_, _ = kafka.InternalEndpoint()
	_, _ = kafka.ConnectionStringExpression()
	if err = kafka.Err(); err != nil {
		log.Fatalf("kafka: %v", err)
	}

	kafka2 := builder.AddKafka("resource", nil)
	kafka2.WithDataBindMount("/tmp", nil)
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
