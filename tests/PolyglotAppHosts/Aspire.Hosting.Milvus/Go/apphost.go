// Aspire Go validation AppHost - Aspire.Hosting.Milvus
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

	milvus, err := builder.AddMilvus("resource", nil, nil)
	if err != nil {
		log.Fatalf("AddMilvus: %v", err)
	}

	param, err := builder.AddParameter("parameter", nil)
	if err != nil {
		log.Fatalf("AddParameter: %v", err)
	}
	milvus2, err := builder.AddMilvus("resource", param, nil)
	if err != nil {
		log.Fatalf("AddMilvus: %v", err)
	}

	_, err = builder.AddMilvus("resource", nil, nil)
	if err != nil {
		log.Fatalf("AddMilvus: %v", err)
	}

	_, err = milvus.AddDatabase("resource", nil)
	if err != nil {
		log.Fatalf("AddDatabase: %v", err)
	}

	_, _ = milvus.AddDatabase("resource", nil)
	milvus.WithAttu(nil, nil)
	milvus2.WithAttu(nil, nil)

	_, err = builder.AddMilvus("resource", nil, nil)
	if err != nil {
		log.Fatalf("AddMilvus: %v", err)
	}

	milvus.WithDataVolume(nil, nil)
	milvus2.WithDataVolume(nil, nil)

	_, err = builder.AddMilvus("resource", nil, nil)
	if err != nil {
		log.Fatalf("AddMilvus: %v", err)
	}

	_, err = builder.AddMilvus("resource", nil, nil)
	if err != nil {
		log.Fatalf("AddMilvus: %v", err)
	}

	_, err = builder.AddMilvus("resource", nil, nil)
	if err != nil {
		log.Fatalf("AddMilvus: %v", err)
	}

	_, err = builder.AddMilvus("resource", nil, nil)
	if err != nil {
		log.Fatalf("AddMilvus: %v", err)
	}

	_, err = builder.AddMilvus("resource", nil, nil)
	if err != nil {
		log.Fatalf("AddMilvus: %v", err)
	}

	api, err := builder.AddContainer("resource", "image")
	if err != nil {
		log.Fatalf("AddContainer: %v", err)
	}
	_, _ = api.WithReference(nil, nil, nil, nil)
	_, _ = api.WithReference(nil, nil, nil, nil)

	_, _ = milvus.PrimaryEndpoint()
	_, _ = milvus.Host()
	_, _ = milvus.Port()
	_, _ = milvus.Token()
	_, _ = milvus.UriExpression()
	_, _ = milvus.ConnectionStringExpression()

	app, err := builder.Build()
	if err != nil {
		log.Fatalf("Build: %v", err)
	}
	if err := app.Run(nil); err != nil {
		log.Fatalf("Run: %v", err)
	}
}
