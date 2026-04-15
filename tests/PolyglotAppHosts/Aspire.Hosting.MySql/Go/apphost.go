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

	rootPassword := builder.AddParameterWithOpts("mysql-root-password",
		&aspire.AddParameterOptions{Secret: aspire.BoolPtr(true)})

	mysql := builder.AddMySqlWithOpts("mysql", &aspire.AddMySqlOptions{
		Password: rootPassword,
		Port:     aspire.Float64Ptr(3306),
	})

	mysql.WithPassword(rootPassword)
	mysql.WithDataVolume()
	mysql.WithDataBindMountWithOpts(".", &aspire.WithDataBindMountOptions{IsReadOnly: aspire.BoolPtr(true)})
	mysql.WithInitFiles(".")

	mysql.WithPhpMyAdminWithOpts(
		&aspire.WithPhpMyAdminOptions{ContainerName: aspire.StringPtr("phpmyadmin")},
		func(container *aspire.PhpMyAdminContainerResource) {
			container.WithHttpEndpointWithOpts(&aspire.WithHttpEndpointOptions{Port: aspire.Float64Ptr(8080)})
		},
	)

	db := mysql.AddDatabaseWithOpts("appdb", &aspire.AddDatabaseOptions{
		DatabaseName: aspire.StringPtr("appdb"),
	})
	db.WithCreationScript("CREATE DATABASE IF NOT EXISTS appdb;")
	if err = db.Err(); err != nil {
		log.Fatalf("db: %v", err)
	}

	_, _ = mysql.PrimaryEndpoint()
	_, _ = mysql.Host()
	_, _ = mysql.Port()
	_, _ = mysql.UriExpression()
	_, _ = mysql.JdbcConnectionString()
	_, _ = mysql.ConnectionStringExpression()
	_ = mysql.Databases()

	if err = mysql.Err(); err != nil {
		log.Fatalf("mysql: %v", err)
	}

	app, err := builder.Build()
	if err != nil {
		log.Fatalf("Build: %v", err)
	}
	if err := app.Run(nil); err != nil {
		log.Fatalf("Run: %v", err)
	}
}
