// Aspire Go validation AppHost - Aspire.Hosting.MongoDB
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

	mongo, err := builder.AddMongoDB("resource", nil, nil, nil)
	if err != nil {
		log.Fatalf("AddMongoDB: %v", err)
	}
	_, _ = mongo.AddDatabase("resource", nil)
	_, _ = mongo.AddDatabase("resource", nil)

	_, err = builder.AddMongoDB("resource", nil, nil, nil)
	if err != nil {
		log.Fatalf("AddMongoDB: %v", err)
	}

	_, err = builder.AddMongoDB("resource", nil, nil, nil)
	if err != nil {
		log.Fatalf("AddMongoDB: %v", err)
	}

	_, err = builder.AddMongoDB("resource", nil, nil, nil)
	if err != nil {
		log.Fatalf("AddMongoDB: %v", err)
	}

	_, err = builder.AddMongoDB("resource", nil, nil, nil)
	if err != nil {
		log.Fatalf("AddMongoDB: %v", err)
	}

	_, _ = builder.AddParameter("parameter", nil)
	_, err = builder.AddMongoDB("resource", nil, nil, nil)
	if err != nil {
		log.Fatalf("AddMongoDB: %v", err)
	}

	mongoChained, err := builder.AddMongoDB("resource", nil, nil, nil)
	if err != nil {
		log.Fatalf("AddMongoDB: %v", err)
	}
	_, _ = mongoChained.AddDatabase("resource", nil)
	_, _ = mongoChained.AddDatabase("resource", nil)

	_, _ = mongo.PrimaryEndpoint()
	_, _ = mongo.Host()
	_, _ = mongo.Port()
	_, _ = mongo.UriExpression()
	_, _ = mongo.UserNameReference()
	_, _ = mongo.ConnectionStringExpression()

	app, err := builder.Build()
	if err != nil {
		log.Fatalf("Build: %v", err)
	}
	if err := app.Run(nil); err != nil {
		log.Fatalf("Run: %v", err)
	}
}
