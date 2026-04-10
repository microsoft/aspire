// Aspire Go validation AppHost - Aspire.Hosting.Nats
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

	nats := builder.AddNats("resource", nil, nil, nil)
	nats.WithJetStream()
	nats.WithDataVolume(nil, nil)
	if err = nats.Err(); err != nil {
		log.Fatalf("nats: %v", err)
	}

	builder.AddNats("resource", nil, nil, nil)

	nats3 := builder.AddNats("resource", nil, nil, nil)
	nats3.WithDataBindMount("/tmp", nil)
	if err = nats3.Err(); err != nil {
		log.Fatalf("nats3: %v", err)
	}

	_, _ = builder.AddParameter("parameter", nil)
	_, _ = builder.AddParameter("parameter", nil)
	builder.AddNats("resource", nil, nil, nil)

	consumer := builder.AddContainer("resource", "image")
	consumer.WithReference(nil, nil, nil, nil)
	consumer.WithReference(nil, nil, nil, nil)
	if err = consumer.Err(); err != nil {
		log.Fatalf("consumer: %v", err)
	}

	_, _ = nats.PrimaryEndpoint()
	_, _ = nats.Host()
	_, _ = nats.Port()
	_, _ = nats.UriExpression()
	_, _ = nats.UserNameReference()
	_, _ = nats.ConnectionStringExpression()

	app, err := builder.Build()
	if err != nil {
		log.Fatalf("Build: %v", err)
	}
	if err := app.Run(nil); err != nil {
		log.Fatalf("Run: %v", err)
	}
}
