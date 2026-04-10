// Aspire Go validation AppHost - Aspire.Hosting.RabbitMQ
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

	rabbitmq := builder.AddRabbitMQ("resource", nil, nil, nil)
	rabbitmq.WithDataVolume(nil, nil)
	rabbitmq.WithManagementPlugin()
	if err = rabbitmq.Err(); err != nil {
		log.Fatalf("rabbitmq: %v", err)
	}

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
