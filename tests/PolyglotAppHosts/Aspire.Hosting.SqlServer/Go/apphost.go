// Aspire Go validation AppHost - Aspire.Hosting.SqlServer
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

	sqlServer := builder.AddSqlServer("resource", nil, nil)
	sqlServer.AddDatabase("resource", nil)
	if err = sqlServer.Err(); err != nil {
		log.Fatalf("sqlServer: %v", err)
	}

	builder.AddSqlServer("resource", nil, nil)
	builder.AddSqlServer("resource", nil, nil)

	_, _ = builder.AddParameter("parameter", nil)
	builder.AddSqlServer("resource", nil, nil)

	sqlChained := builder.AddSqlServer("resource", nil, nil)
	sqlChained.AddDatabase("resource", nil)
	sqlChained.AddDatabase("resource", nil)
	if err = sqlChained.Err(); err != nil {
		log.Fatalf("sqlChained: %v", err)
	}

	_, _ = sqlServer.PrimaryEndpoint()
	_, _ = sqlServer.Host()
	_, _ = sqlServer.Port()
	_, _ = sqlServer.UriExpression()
	_, _ = sqlServer.JdbcConnectionString()
	_, _ = sqlServer.UserNameReference()
	_, _ = sqlServer.ConnectionStringExpression()

	app, err := builder.Build()
	if err != nil {
		log.Fatalf("Build: %v", err)
	}
	if err := app.Run(nil); err != nil {
		log.Fatalf("Run: %v", err)
	}
}
