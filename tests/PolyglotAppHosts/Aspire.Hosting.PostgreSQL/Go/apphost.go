// Aspire Go validation AppHost - Aspire.Hosting.PostgreSQL
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

	postgres, err := builder.AddPostgres("resource", nil, nil, nil)
	if err != nil {
		log.Fatalf("AddPostgres: %v", err)
	}

	db, err := postgres.AddDatabase("resource", nil)
	if err != nil {
		log.Fatalf("AddDatabase: %v", err)
	}

	postgres.WithPgAdmin(nil, nil)
	postgres.WithPgAdmin(nil, nil)
	postgres.WithPgWeb(nil, nil)
	postgres.WithPgWeb(nil, nil)
	postgres.WithDataVolume(nil, nil)
	postgres.WithDataVolume(nil, nil)
	postgres.WithDataBindMount("/tmp", nil)
	postgres.WithDataBindMount("/tmp", nil)
	postgres.WithInitFiles("./init")
	postgres.WithHostPort(5432)
	db.WithCreationScript("script.sql")

	_, _ = builder.AddParameter("parameter", nil)
	_, _ = builder.AddParameter("parameter", nil)
	pg2, err := builder.AddPostgres("resource", nil, nil, nil)
	if err != nil {
		log.Fatalf("AddPostgres: %v", err)
	}
	pg2.WithPassword(nil)
	pg2.WithUserName(nil)

	_, _ = postgres.PrimaryEndpoint()
	_, _ = postgres.UserNameReference()
	_, _ = postgres.UriExpression()
	_, _ = postgres.JdbcConnectionString()
	_, _ = postgres.ConnectionStringExpression()
	_, _ = db.DatabaseName()
	_, _ = db.UriExpression()
	_, _ = db.JdbcConnectionString()
	_, _ = db.ConnectionStringExpression()

	app, err := builder.Build()
	if err != nil {
		log.Fatalf("Build: %v", err)
	}
	if err := app.Run(nil); err != nil {
		log.Fatalf("Run: %v", err)
	}
}
