// Aspire Go validation AppHost - Aspire.Hosting.Azure.Sql
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

	azStorage := builder.AddAzureStorage("resource")
	if err = azStorage.Err(); err != nil {
		log.Fatalf("azStorage: %v", err)
	}

	sqlServer := builder.AddAzureSqlServer("resource")

	db := sqlServer.AddDatabase("resource", nil)
	_ = db

	db2 := sqlServer.AddDatabase("resource", nil)
	db2.WithDefaultAzureSku()
	if err = db2.Err(); err != nil {
		log.Fatalf("db2: %v", err)
	}

	sqlServer.RunAsContainer(nil)
	sqlServer.WithAdminDeploymentScriptStorage(azStorage)
	sqlServer.AddDatabase("resource", nil)

	_, _ = sqlServer.HostName()
	_, _ = sqlServer.Port()
	_, _ = sqlServer.UriExpression()
	_, _ = sqlServer.ConnectionStringExpression()
	_, _ = sqlServer.JdbcConnectionString()
	_, _ = sqlServer.IsContainer()
	if err = sqlServer.Err(); err != nil {
		log.Fatalf("sqlServer: %v", err)
	}

	app, err := builder.Build()
	if err != nil {
		log.Fatalf("Build: %v", err)
	}
	if err := app.Run(nil); err != nil {
		log.Fatalf("Run: %v", err)
	}
}
