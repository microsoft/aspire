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

	nats, err := builder.AddNats("resource")
	if err != nil {
		log.Fatalf("AddNats: %v", err)
	}
	_, _ = nats.WithJetStream()
	_, _ = nats.WithDataVolume()

	_, err = builder.AddNats("resource")
	if err != nil {
		log.Fatalf("AddNats: %v", err)
	}

	nats3, err := builder.AddNats("resource")
	if err != nil {
		log.Fatalf("AddNats: %v", err)
	}
	_, _ = nats3.WithDataBindMount()

	_, _ = builder.AddParameter("parameter")
	_, _ = builder.AddParameter("parameter")
	_, err = builder.AddNats("resource")
	if err != nil {
		log.Fatalf("AddNats: %v", err)
	}

	consumer, err := builder.AddContainer("resource", "image")
	if err != nil {
		log.Fatalf("AddContainer: %v", err)
	}
	_, _ = consumer.WithReference()
	_, _ = consumer.WithReference()

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
