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

	postgres := builder.AddPostgres("resource", nil, nil, nil)
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
	if err = postgres.Err(); err != nil {
		log.Fatalf("postgres: %v", err)
	}

	db := postgres.AddDatabase("resource", nil)
	db.WithCreationScript("script.sql")
	if err = db.Err(); err != nil {
		log.Fatalf("db: %v", err)
	}

	_, _ = builder.AddParameter("parameter", nil)
	_, _ = builder.AddParameter("parameter", nil)
	pg2 := builder.AddPostgres("resource", nil, nil, nil)
	pg2.WithPassword(nil)
	pg2.WithUserName(nil)
	if err = pg2.Err(); err != nil {
		log.Fatalf("pg2: %v", err)
	}

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
