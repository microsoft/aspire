// Aspire Go validation AppHost - Aspire.Hosting.Azure.Kusto
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

	kusto := builder.AddAzureKustoCluster("resource")

	defaultDatabase := kusto.AddReadWriteDatabase("resource", nil)

	customDatabase := kusto.AddReadWriteDatabase("resource", nil)

	defaultDatabase.WithCreationScript("./script.kql")
	customDatabase.WithCreationScript("./script.kql")

	_, _ = kusto.IsEmulator()
	_, _ = kusto.UriExpression()
	_, _ = kusto.ConnectionStringExpression()
	_, _ = defaultDatabase.DatabaseName()
	_, _ = defaultDatabase.Parent()
	_, _ = defaultDatabase.ConnectionStringExpression()
	_, _ = defaultDatabase.GetDatabaseCreationScript()
	_, _ = customDatabase.DatabaseName()
	_, _ = customDatabase.Parent()
	_, _ = customDatabase.ConnectionStringExpression()
	_, _ = customDatabase.GetDatabaseCreationScript()

	app, err := builder.Build()
	if err != nil {
		log.Fatalf("Build: %v", err)
	}
	if err := app.Run(nil); err != nil {
		log.Fatalf("Run: %v", err)
	}
}
