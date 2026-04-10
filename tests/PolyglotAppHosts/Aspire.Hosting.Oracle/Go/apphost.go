// Aspire Go validation AppHost - Aspire.Hosting.Oracle
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

	oracle, err := builder.AddOracle("resource", nil, nil)
	if err != nil {
		log.Fatalf("AddOracle: %v", err)
	}

	_, _ = builder.AddParameter("parameter", nil)
	_, err = builder.AddOracle("resource", nil, nil)
	if err != nil {
		log.Fatalf("AddOracle: %v", err)
	}

	db, err := oracle.AddDatabase("resource", nil)
	if err != nil {
		log.Fatalf("AddDatabase: %v", err)
	}
	_ = db

	_, _ = oracle.AddDatabase("resource", nil)
	oracle.WithDataVolume(nil)

	oracle2, err := builder.AddOracle("resource", nil, nil)
	if err != nil {
		log.Fatalf("AddOracle: %v", err)
	}
	oracle2.WithDataVolume(nil)
	oracle2.WithDataBindMount("/tmp")
	oracle2.WithInitFiles("./init")
	oracle2.WithDbSetupBindMount("/tmp")
	_, _ = oracle.WithReference(nil, nil, nil, nil)
	_, _ = oracle.WithReference(nil, nil, nil, nil)
	_, _ = oracle.WithReference(nil, nil, nil, nil)

	oracle3, err := builder.AddOracle("resource", nil, nil)
	if err != nil {
		log.Fatalf("AddOracle: %v", err)
	}
	_, _ = oracle3.AddDatabase("resource", nil)

	_, _ = oracle.PrimaryEndpoint()
	_, _ = oracle.Host()
	_, _ = oracle.Port()
	_, _ = oracle.UserNameReference()
	_, _ = oracle.UriExpression()
	_, _ = oracle.JdbcConnectionString()
	_, _ = oracle.ConnectionStringExpression()

	app, err := builder.Build()
	if err != nil {
		log.Fatalf("Build: %v", err)
	}
	if err := app.Run(nil); err != nil {
		log.Fatalf("Run: %v", err)
	}
}
