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

	rootPassword := builder.AddParameter("mysql-root-password",
		&aspire.AddParameterOptions{Secret: aspire.BoolPtr(true)})

	mysql := builder.AddMySql("mysql", &aspire.AddMySqlOptions{
		Password: &rootPassword,
		Port:     aspire.Float64Ptr(3306),
	})

	mysql.WithPassword(rootPassword)
	mysql.WithDataVolume()
	mysql.WithDataBindMount(".", &aspire.WithDataBindMountOptions{IsReadOnly: aspire.BoolPtr(true)})
	mysql.WithInitFiles(".")

	mysql.WithPhpMyAdmin(&aspire.WithPhpMyAdminOptions{
		ContainerName: aspire.StringPtr("phpmyadmin"),
		ConfigureContainer: func(container aspire.PhpMyAdminContainerResource) {
			container.WithHostPort(8080)
		},
	})

	db := mysql.AddDatabase("appdb", &aspire.AddDatabaseOptions{
		DatabaseName: aspire.StringPtr("appdb"),
	})
	db.WithCreationScript("CREATE DATABASE IF NOT EXISTS appdb;")
	if err = db.Err(); err != nil {
		log.Fatalf(aspire.FormatError(err))
	}

	_ = mysql.PrimaryEndpoint()
	_ = mysql.Host()
	_ = mysql.Port()
	_ = mysql.UriExpression()
	_ = mysql.JdbcConnectionString()
	_ = mysql.ConnectionStringExpression()
	_, _ = mysql.Databases()

	if err = mysql.Err(); err != nil {
		log.Fatalf(aspire.FormatError(err))
	}

	app, err := builder.Build()
	if err != nil {
		log.Fatalf(aspire.FormatError(err))
	}
	if err := app.Run(nil); err != nil {
		log.Fatalf(aspire.FormatError(err))
	}
}
