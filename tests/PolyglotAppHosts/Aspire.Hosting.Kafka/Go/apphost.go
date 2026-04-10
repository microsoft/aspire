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

	kafka, err := builder.AddKafka("resource")
	if err != nil {
		log.Fatalf("AddKafka: %v", err)
	}

	kafkaWithUI, err := kafka.WithKafkaUI()
	if err != nil {
		log.Fatalf("WithKafkaUI: %v", err)
	}
	_, _ = kafkaWithUI.WithDataVolume()

	kafka2, err := builder.AddKafka("resource")
	if err != nil {
		log.Fatalf("AddKafka: %v", err)
	}
	_, _ = kafka2.WithDataBindMount()

	_, _ = kafka.PrimaryEndpoint()
	_, _ = kafka.Host()
	_, _ = kafka.Port()
	_, _ = kafka.InternalEndpoint()
	_, _ = kafka.ConnectionStringExpression()

	app, err := builder.Build()
	if err != nil {
		log.Fatalf("Build: %v", err)
	}
	if err := app.Run(nil); err != nil {
		log.Fatalf("Run: %v", err)
	}
}
