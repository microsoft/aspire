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

	// Test 1: Basic SQL Server resource creation
	sqlServer := builder.AddSqlServer("sql")

	// Test 2: Add database to SQL Server
	sqlServer.AddDatabase("mydb")
	if err = sqlServer.Err(); err != nil {
		log.Fatalf("sqlServer: %v", err)
	}

	// Test 3: Test withDataVolume
	builder.AddSqlServer("sql-volume").WithDataVolume()

	// Test 4: Test withHostPort
	builder.AddSqlServer("sql-port").WithHostPort(11433)

	// Test 5: Test password parameter
	customPassword := builder.AddParameterWithOpts("sql-password", &aspire.AddParameterOptions{Secret: func() *bool { b := true; return &b }()})
	builder.AddSqlServerWithOpts("sql-custom-pass", &aspire.AddSqlServerOptions{Password: customPassword})

	// Test 6: Chained configuration - multiple With* methods
	sqlChained := builder.AddSqlServer("sql-chained")
	sqlChained.WithLifetime(aspire.ContainerLifetimePersistent)
	sqlChained.WithDataVolumeWithOpts(&aspire.WithDataVolumeOptions{Name: func() *string { s := "sql-chained-data"; return &s }()})
	sqlChained.WithHostPort(12433)

	// Test 7: Add multiple databases to same server
	sqlChained.AddDatabase("db1")
	sqlChained.AddDatabaseWithOpts("db2", &aspire.AddDatabaseOptions{DatabaseName: func() *string { s := "customdb2"; return &s }()})
	if err = sqlChained.Err(); err != nil {
		log.Fatalf("sqlChained: %v", err)
	}

	// ---- Property access on SqlServerServerResource ----
	_, _ = sqlServer.PrimaryEndpoint()
	_, _ = sqlServer.Host()
	_, _ = sqlServer.Port()
	_, _ = sqlServer.UriExpression()
	_, _ = sqlServer.JdbcConnectionString()
	_, _ = sqlServer.UserNameReference()
	_, _ = sqlServer.ConnectionStringExpression()
	_ = sqlServer.Databases()

	app, err := builder.Build()
	if err != nil {
		log.Fatalf("Build: %v", err)
	}
	if err := app.Run(nil); err != nil {
		log.Fatalf("Run: %v", err)
	}
}
