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

	azStorage, err := builder.AddAzureStorage("resource")
	if err != nil {
		log.Fatalf("AddAzureStorage: %v", err)
	}

	sqlServer, err := builder.AddAzureSqlServer("resource")
	if err != nil {
		log.Fatalf("AddAzureSqlServer: %v", err)
	}

	db, err := sqlServer.AddDatabase("resource", nil)
	if err != nil {
		log.Fatalf("AddDatabase: %v", err)
	}
	_ = db

	db2, err := sqlServer.AddDatabase("resource", nil)
	if err != nil {
		log.Fatalf("AddDatabase: %v", err)
	}
	db2.WithDefaultAzureSku()

	sqlServer.RunAsContainer(nil)
	sqlServer.WithAdminDeploymentScriptStorage(azStorage)
	_, _ = sqlServer.AddDatabase("resource", nil)

	_, _ = sqlServer.HostName()
	_, _ = sqlServer.Port()
	_, _ = sqlServer.UriExpression()
	_, _ = sqlServer.ConnectionStringExpression()
	_, _ = sqlServer.JdbcConnectionString()
	_, _ = sqlServer.IsContainer()

	app, err := builder.Build()
	if err != nil {
		log.Fatalf("Build: %v", err)
	}
	if err := app.Run(nil); err != nil {
		log.Fatalf("Run: %v", err)
	}
}
