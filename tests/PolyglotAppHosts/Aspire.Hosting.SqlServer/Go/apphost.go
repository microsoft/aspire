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

	sqlServer, err := builder.AddSqlServer("resource", nil, nil)
	if err != nil {
		log.Fatalf("AddSqlServer: %v", err)
	}
	_, _ = sqlServer.AddDatabase("resource", nil)

	_, err = builder.AddSqlServer("resource", nil, nil)
	if err != nil {
		log.Fatalf("AddSqlServer: %v", err)
	}

	_, err = builder.AddSqlServer("resource", nil, nil)
	if err != nil {
		log.Fatalf("AddSqlServer: %v", err)
	}

	_, _ = builder.AddParameter("parameter", nil)
	_, err = builder.AddSqlServer("resource", nil, nil)
	if err != nil {
		log.Fatalf("AddSqlServer: %v", err)
	}

	sqlChained, err := builder.AddSqlServer("resource", nil, nil)
	if err != nil {
		log.Fatalf("AddSqlServer: %v", err)
	}
	_, _ = sqlChained.AddDatabase("resource", nil)
	_, _ = sqlChained.AddDatabase("resource", nil)

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
