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

	nats, err := builder.AddNats("resource", nil, nil, nil)
	if err != nil {
		log.Fatalf("AddNats: %v", err)
	}
	nats.WithJetStream()
	nats.WithDataVolume(nil, nil)

	_, err = builder.AddNats("resource", nil, nil, nil)
	if err != nil {
		log.Fatalf("AddNats: %v", err)
	}

	nats3, err := builder.AddNats("resource", nil, nil, nil)
	if err != nil {
		log.Fatalf("AddNats: %v", err)
	}
	nats3.WithDataBindMount("/tmp", nil)

	_, _ = builder.AddParameter("parameter", nil)
	_, _ = builder.AddParameter("parameter", nil)
	_, err = builder.AddNats("resource", nil, nil, nil)
	if err != nil {
		log.Fatalf("AddNats: %v", err)
	}

	consumer, err := builder.AddContainer("resource", "image")
	if err != nil {
		log.Fatalf("AddContainer: %v", err)
	}
	_, _ = consumer.WithReference(nil, nil, nil, nil)
	_, _ = consumer.WithReference(nil, nil, nil, nil)

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
