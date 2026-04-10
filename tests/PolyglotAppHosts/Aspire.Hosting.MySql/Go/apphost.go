// Aspire Go validation AppHost - Aspire.Hosting.MySql
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

	_, _ = builder.AddParameter("parameter", nil)

	mysql := builder.AddMySql("resource", nil, nil)
	mysql.WithPhpMyAdmin(nil, nil)
	if err = mysql.Err(); err != nil {
		log.Fatalf("mysql: %v", err)
	}

	db := mysql.AddDatabase("resource", nil)
	db.WithCreationScript("script.sql")
	if err = db.Err(); err != nil {
		log.Fatalf("db: %v", err)
	}

	_, _ = mysql.PrimaryEndpoint()
	_, _ = mysql.Host()
	_, _ = mysql.Port()
	_, _ = mysql.UriExpression()
	_, _ = mysql.JdbcConnectionString()
	_, _ = mysql.ConnectionStringExpression()

	app, err := builder.Build()
	if err != nil {
		log.Fatalf("Build: %v", err)
	}
	if err := app.Run(nil); err != nil {
		log.Fatalf("Run: %v", err)
	}
}
