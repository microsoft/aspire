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

	// VNet with subnets (validates #15373 fix)
	vnet := builder.AddAzureVirtualNetwork("vnet")
	deploymentSubnet := vnet.AddSubnet("deployment-subnet", "10.0.1.0/24")
	aciSubnet := vnet.AddSubnet("aci-subnet", "10.0.2.0/29")

	// ── 1. Storage resource (for admin deployment script) ────────────────────
	storage := builder.AddAzureStorage("storage")
	if err = storage.Err(); err != nil {
		log.Fatalf("storage: %v", err)
	}

	// ── 2. SQL Server + databases ─────────────────────────────────────────────
	sqlServer := builder.AddAzureSqlServer("sql")

	// simple addDatabase — no options
	db := sqlServer.AddDatabase("mydb")
	_ = db

	// addDatabase with databaseName option
	db2 := sqlServer.AddDatabaseWithOpts("inventory",
		&aspire.AddDatabaseOptions{DatabaseName: aspire.StringPtr("inventorydb")})
	db2.WithDefaultAzureSku()
	if err = db2.Err(); err != nil {
		log.Fatalf("db2: %v", err)
	}

	// ── 3. runAsContainer ─────────────────────────────────────────────────────
	sqlServer.RunAsContainer(nil)

	// ── 4. withAdminDeploymentScriptSubnet / Storage ──────────────────────────
	sqlServer.WithAdminDeploymentScriptSubnet(deploymentSubnet)
	sqlServer.WithAdminDeploymentScriptStorage(storage)
	sqlServer.WithAdminDeploymentScriptSubnet(aciSubnet)

	// ── 5. fluent chain: addDatabase().withDefaultAzureSku() ─────────────────
	_ = sqlServer.AddDatabase("analytics").WithDefaultAzureSku()

	// ── 6. server property accessors ─────────────────────────────────────────
	_, _ = sqlServer.HostName()
	_, _ = sqlServer.Port()
	_, _ = sqlServer.UriExpression()
	_, _ = sqlServer.ConnectionStringExpression()
	_, _ = sqlServer.JdbcConnectionString()
	_, _ = sqlServer.FullyQualifiedDomainName()
	_, _ = sqlServer.NameOutputReference()
	_, _ = sqlServer.Id()
	_, _ = sqlServer.IsContainer()
	_ = sqlServer.Databases()
	_ = sqlServer.AzureSqlDatabases()

	if err = sqlServer.Err(); err != nil {
		log.Fatalf("sqlServer: %v", err)
	}

	// ── 7. database property accessors ────────────────────────────────────────
	_, _ = db.Parent()
	_, _ = db.ConnectionStringExpression()
	_, _ = db.DatabaseName()
	_, _ = db.IsContainer()
	_, _ = db.UriExpression()
	_, _ = db.JdbcConnectionString()
	if err = db.Err(); err != nil {
		log.Fatalf("db: %v", err)
	}

	app, err := builder.Build()
	if err != nil {
		log.Fatalf("Build: %v", err)
	}
	if err := app.Run(nil); err != nil {
		log.Fatalf("Run: %v", err)
	}
}
