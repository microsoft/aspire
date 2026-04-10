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

	oracle, err := builder.AddOracle("resource")
	if err != nil {
		log.Fatalf("AddOracle: %v", err)
	}

	_, _ = builder.AddParameter("parameter")
	_, err = builder.AddOracle("resource")
	if err != nil {
		log.Fatalf("AddOracle: %v", err)
	}

	db, err := oracle.AddDatabase("resource")
	if err != nil {
		log.Fatalf("AddDatabase: %v", err)
	}
	_ = db

	_, _ = oracle.AddDatabase("resource")
	_, _ = oracle.WithDataVolume()

	oracle2, err := builder.AddOracle("resource")
	if err != nil {
		log.Fatalf("AddOracle: %v", err)
	}
	_, _ = oracle2.WithDataVolume()
	_, _ = oracle2.WithDataBindMount()
	_, _ = oracle2.WithInitFiles()
	_, _ = oracle2.WithDbSetupBindMount()
	_, _ = oracle.WithReference()
	_, _ = oracle.WithReference()
	_, _ = oracle.WithReference()

	oracle3, err := builder.AddOracle("resource")
	if err != nil {
		log.Fatalf("AddOracle: %v", err)
	}
	_, _ = oracle3.AddDatabase("resource")

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
