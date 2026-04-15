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

	// ---- AddPostgres: factory method ----
	postgres := builder.AddPostgres("pg")

	// ---- WithPgAdmin: management UI ----
	postgres.WithPgAdmin(nil)
	postgres.WithPgAdminWithOpts(&aspire.WithPgAdminOptions{ContainerName: func() *string { s := "mypgadmin"; return &s }()}, nil)

	// ---- WithPgWeb: management UI ----
	postgres.WithPgWeb(nil)
	postgres.WithPgWebWithOpts(&aspire.WithPgWebOptions{ContainerName: func() *string { s := "mypgweb"; return &s }()}, nil)

	// ---- WithDataVolume: data persistence ----
	postgres.WithDataVolume()
	postgres.WithDataVolumeWithOpts(&aspire.WithDataVolumeOptions{Name: func() *string { s := "pg-data"; return &s }(), IsReadOnly: func() *bool { b := false; return &b }()})

	// ---- WithDataBindMount: bind mount ----
	postgres.WithDataBindMount("./data")
	postgres.WithDataBindMountWithOpts("./data2", &aspire.WithDataBindMountOptions{IsReadOnly: func() *bool { b := true; return &b }()})

	// ---- WithInitFiles: initialization scripts ----
	postgres.WithInitFiles("./init")

	// ---- WithHostPort: explicit port ----
	postgres.WithHostPort(5432)

	if err = postgres.Err(); err != nil {
		log.Fatalf("postgres: %v", err)
	}

	// ---- AddDatabase: child resource ----
	db := postgres.AddDatabaseWithOpts("mydb", &aspire.AddDatabaseOptions{DatabaseName: func() *string { s := "testdb"; return &s }()})

	// ---- WithCreationScript: custom database creation SQL ----
	db.WithCreationScript(`CREATE DATABASE "testdb"`)
	if err = db.Err(); err != nil {
		log.Fatalf("db: %v", err)
	}

	// ---- WithPassword / WithUserName: credential configuration ----
	customPassword := builder.AddParameterWithOpts("pg-password", &aspire.AddParameterOptions{Secret: func() *bool { b := true; return &b }()})
	customUser := builder.AddParameter("pg-user")
	pg2 := builder.AddPostgres("pg2")
	pg2.WithPassword(customPassword)
	pg2.WithUserName(customUser)
	if err = pg2.Err(); err != nil {
		log.Fatalf("pg2: %v", err)
	}

	// ---- Property access on PostgresServerResource ----
	_, _ = postgres.PrimaryEndpoint()
	_, _ = postgres.UserNameReference()
	_, _ = postgres.UriExpression()
	_, _ = postgres.JdbcConnectionString()
	_, _ = postgres.ConnectionStringExpression()

	// ---- Property access on PostgresDatabaseResource ----
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
