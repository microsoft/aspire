package main

import (
	"log"

	"apphost/modules/aspire"
)

func main() {
	builder, err := aspire.CreateBuilder(nil)
	if err != nil {
		log.Fatalf(aspire.FormatError(err))
	}

	// 1) AddAzurePostgresFlexibleServer — main factory method
	pg := builder.AddAzurePostgresFlexibleServer("pg")
	if pg.Err() != nil {
		log.Fatalf(aspire.FormatError(pg.Err()))
	}

	// 2) AddDatabase — child resource
	db := pg.AddDatabase("mydb", &aspire.AddDatabaseOptions{
		DatabaseName: aspire.StringPtr("appdb"),
	})
	if db.Err() != nil {
		log.Fatalf(aspire.FormatError(db.Err()))
	}

	// 3) WithPasswordAuthentication
	pgAuth := builder.AddAzurePostgresFlexibleServer("pg-auth")
	if pgAuth.Err() != nil {
		log.Fatalf(aspire.FormatError(pgAuth.Err()))
	}
	pgAuth.WithPasswordAuthentication()

	// 4) RunAsContainer — run as local PostgreSQL container
	pgContainer := builder.AddAzurePostgresFlexibleServer("pg-container")
	if pgContainer.Err() != nil {
		log.Fatalf(aspire.FormatError(pgContainer.Err()))
	}
	pgContainer.RunAsContainer(&aspire.RunAsContainerOptions{
		ConfigureContainer: func(container aspire.PostgresServerResource) {
			container.WithLifetime(aspire.ContainerLifetimePersistent)
		},
	})
	if pgContainer.Err() != nil {
		log.Fatalf(aspire.FormatError(pgContainer.Err()))
	}

	// 5) AddDatabase on container-mode server
	dbContainer := pgContainer.AddDatabase("containerdb")
	if dbContainer.Err() != nil {
		log.Fatalf(aspire.FormatError(dbContainer.Err()))
	}

	app, err := builder.Build()
	if err != nil {
		log.Fatalf(aspire.FormatError(err))
	}
	if err := app.Run(nil); err != nil {
		log.Fatalf(aspire.FormatError(err))
	}
}
