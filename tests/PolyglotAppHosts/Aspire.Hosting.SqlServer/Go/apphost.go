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

	sqlServer := builder.AddSqlServer("sql")

	sqlServer.AddDatabase("mydb")
	if err = sqlServer.Err(); err != nil {
		log.Fatalf(aspire.FormatError(err))
	}

	builder.AddSqlServer("sql-volume").WithDataVolume()

	builder.AddSqlServer("sql-port").WithHostPort(11433)

	customPassword := builder.AddParameter("sql-password", &aspire.AddParameterOptions{Secret: aspire.BoolPtr(true)})
	builder.AddSqlServer("sql-custom-pass", &aspire.AddSqlServerOptions{Password: &customPassword})

	sqlChained := builder.AddSqlServer("sql-chained")
	sqlChained.WithLifetime(aspire.ContainerLifetimePersistent)
	sqlChained.WithDataVolume(&aspire.WithDataVolumeOptions{Name: aspire.StringPtr("sql-chained-data")})
	sqlChained.WithHostPort(12433)

	sqlChained.AddDatabase("db1")
	sqlChained.AddDatabase("db2", &aspire.AddDatabaseOptions{DatabaseName: aspire.StringPtr("customdb2")})
	if err = sqlChained.Err(); err != nil {
		log.Fatalf(aspire.FormatError(err))
	}

	_ = sqlServer.PrimaryEndpoint()
	_ = sqlServer.Host()
	_ = sqlServer.Port()
	_ = sqlServer.UriExpression()
	_ = sqlServer.JdbcConnectionString()
	_ = sqlServer.UserNameReference()
	_ = sqlServer.ConnectionStringExpression()
	_, _ = sqlServer.Databases()

	app, err := builder.Build()
	if err != nil {
		log.Fatalf(aspire.FormatError(err))
	}
	if err := app.Run(nil); err != nil {
		log.Fatalf(aspire.FormatError(err))
	}
}
