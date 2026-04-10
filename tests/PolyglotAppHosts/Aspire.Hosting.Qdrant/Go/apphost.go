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
	builder.AddQdrant("resource", nil, nil, nil)

	qdrant := builder.AddQdrant("resource", nil, nil, nil)
	qdrant.WithDataVolume(nil, nil)
	if err = qdrant.Err(); err != nil {
		log.Fatalf("qdrant: %v", err)
	}

	consumer := builder.AddContainer("resource", "image")
	consumer.WithReference(nil, nil, nil, nil)
	if err = consumer.Err(); err != nil {
		log.Fatalf("consumer: %v", err)
	}

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
