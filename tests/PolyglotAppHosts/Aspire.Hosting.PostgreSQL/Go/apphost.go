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

	postgres := builder.AddPostgres("pg")

	db := postgres.AddDatabase("mydb", &aspire.AddDatabaseOptions{DatabaseName: aspire.StringPtr("testdb")})

	postgres.WithPgAdmin()
	postgres.WithPgAdmin(&aspire.WithPgAdminOptions{ContainerName: aspire.StringPtr("mypgadmin")})

	postgres.WithPgWeb()
	postgres.WithPgWeb(&aspire.WithPgWebOptions{ContainerName: aspire.StringPtr("mypgweb")})

	postgres.WithDataVolume()
	postgres.WithDataVolume(&aspire.WithDataVolumeOptions{Name: aspire.StringPtr("pg-data"), IsReadOnly: aspire.BoolPtr(false)})

	postgres.WithDataBindMount("./data")
	postgres.WithDataBindMount("./data2", &aspire.WithDataBindMountOptions{IsReadOnly: aspire.BoolPtr(true)})

	postgres.WithInitFiles("./init")

	postgres.WithHostPort(5432)

	if err = postgres.Err(); err != nil {
		log.Fatalf(aspire.FormatError(err))
	}

	db.WithCreationScript(`CREATE DATABASE "testdb"`)
	if err = db.Err(); err != nil {
		log.Fatalf(aspire.FormatError(err))
	}

	customPassword := builder.AddParameter("pg-password", &aspire.AddParameterOptions{Secret: aspire.BoolPtr(true)})
	customUser := builder.AddParameter("pg-user")
	pg2 := builder.AddPostgres("pg2")
	pg2.WithPassword(customPassword)
	pg2.WithUserName(customUser)
	if err = pg2.Err(); err != nil {
		log.Fatalf(aspire.FormatError(err))
	}

	_ = postgres.PrimaryEndpoint()
	_ = postgres.UserNameReference()
	_ = postgres.UriExpression()
	_ = postgres.JdbcConnectionString()
	_ = postgres.ConnectionStringExpression()

	_, _ = db.DatabaseName()
	_ = db.UriExpression()
	_ = db.JdbcConnectionString()
	_ = db.ConnectionStringExpression()

	app, err := builder.Build()
	if err != nil {
		log.Fatalf(aspire.FormatError(err))
	}
	if err := app.Run(nil); err != nil {
		log.Fatalf(aspire.FormatError(err))
	}
}
