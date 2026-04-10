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

	mongo := builder.AddMongoDB("resource", nil, nil, nil)
	mongo.AddDatabase("resource", nil)
	mongo.AddDatabase("resource", nil)
	if err = mongo.Err(); err != nil {
		log.Fatalf("mongo: %v", err)
	}

	builder.AddMongoDB("resource", nil, nil, nil)
	builder.AddMongoDB("resource", nil, nil, nil)
	builder.AddMongoDB("resource", nil, nil, nil)
	builder.AddMongoDB("resource", nil, nil, nil)

	_, _ = builder.AddParameter("parameter", nil)
	builder.AddMongoDB("resource", nil, nil, nil)

	mongoChained := builder.AddMongoDB("resource", nil, nil, nil)
	mongoChained.AddDatabase("resource", nil)
	mongoChained.AddDatabase("resource", nil)
	if err = mongoChained.Err(); err != nil {
		log.Fatalf("mongoChained: %v", err)
	}

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
