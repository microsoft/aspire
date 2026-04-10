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

	postgres, err := builder.AddPostgres("resource")
	if err != nil {
		log.Fatalf("AddPostgres: %v", err)
	}

	db, err := postgres.AddDatabase("resource")
	if err != nil {
		log.Fatalf("AddDatabase: %v", err)
	}

	_, _ = postgres.WithPgAdmin()
	_, _ = postgres.WithPgAdmin()
	_, _ = postgres.WithPgWeb()
	_, _ = postgres.WithPgWeb()
	_, _ = postgres.WithDataVolume()
	_, _ = postgres.WithDataVolume()
	_, _ = postgres.WithDataBindMount()
	_, _ = postgres.WithDataBindMount()
	_, _ = postgres.WithInitFiles()
	_, _ = postgres.WithHostPort()
	_, _ = db.WithCreationScript()

	_, _ = builder.AddParameter("parameter")
	_, _ = builder.AddParameter("parameter")
	pg2, err := builder.AddPostgres("resource")
	if err != nil {
		log.Fatalf("AddPostgres: %v", err)
	}
	_, _ = pg2.WithPassword()
	_, _ = pg2.WithUserName()

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
