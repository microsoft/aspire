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

	oracle := builder.AddOracle("resource", nil, nil)
	if err = oracle.Err(); err != nil {
		log.Fatalf("oracle: %v", err)
	}

	_, _ = builder.AddParameter("parameter", nil)
	builder.AddOracle("resource", nil, nil)

	db := oracle.AddDatabase("resource", nil)
	if err = db.Err(); err != nil {
		log.Fatalf("db: %v", err)
	}
	_ = db

	oracle.AddDatabase("resource", nil)
	oracle.WithDataVolume(nil)
	oracle.WithReference(nil, nil, nil, nil)
	oracle.WithReference(nil, nil, nil, nil)
	oracle.WithReference(nil, nil, nil, nil)

	oracle2 := builder.AddOracle("resource", nil, nil)
	oracle2.WithDataVolume(nil)
	oracle2.WithDataBindMount("/tmp")
	oracle2.WithInitFiles("./init")
	oracle2.WithDbSetupBindMount("/tmp")
	if err = oracle2.Err(); err != nil {
		log.Fatalf("oracle2: %v", err)
	}

	oracle3 := builder.AddOracle("resource", nil, nil)
	oracle3.AddDatabase("resource", nil)
	if err = oracle3.Err(); err != nil {
		log.Fatalf("oracle3: %v", err)
	}

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
