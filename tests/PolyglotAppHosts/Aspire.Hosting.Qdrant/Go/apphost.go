// Aspire Go validation AppHost - Aspire.Hosting.Qdrant
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

	_, _ = builder.AddParameter("parameter", nil)
	_, err = builder.AddQdrant("resource", nil, nil, nil)
	if err != nil {
		log.Fatalf("AddQdrant: %v", err)
	}

	qdrant, err := builder.AddQdrant("resource", nil, nil, nil)
	if err != nil {
		log.Fatalf("AddQdrant: %v", err)
	}
	qdrant.WithDataVolume(nil, nil)

	consumer, err := builder.AddContainer("resource", "image")
	if err != nil {
		log.Fatalf("AddContainer: %v", err)
	}
	_, _ = consumer.WithReference(nil, nil, nil, nil)

	_, _ = qdrant.PrimaryEndpoint()
	_, _ = qdrant.GrpcHost()
	_, _ = qdrant.GrpcPort()
	_, _ = qdrant.HttpEndpoint()
	_, _ = qdrant.HttpHost()
	_, _ = qdrant.HttpPort()
	_, _ = qdrant.UriExpression()
	_, _ = qdrant.HttpUriExpression()
	_, _ = qdrant.ConnectionStringExpression()

	app, err := builder.Build()
	if err != nil {
		log.Fatalf("Build: %v", err)
	}
	if err := app.Run(nil); err != nil {
		log.Fatalf("Run: %v", err)
	}
}
