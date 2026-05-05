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

	oracle := builder.AddOracle("oracledb")
	if err = oracle.Err(); err != nil {
		log.Fatalf(aspire.FormatError(err))
	}

	customPassword := builder.AddParameter("oracle-password", &aspire.AddParameterOptions{Secret: aspire.BoolPtr(true)})
	oracle2 := builder.AddOracle("oracledb2", &aspire.AddOracleOptions{
		Password: &customPassword,
		Port:     aspire.Float64Ptr(1522),
	})
	if err = oracle2.Err(); err != nil {
		log.Fatalf(aspire.FormatError(err))
	}

	db := oracle.AddDatabase("mydb")
	if err = db.Err(); err != nil {
		log.Fatalf(aspire.FormatError(err))
	}

	oracle.AddDatabase("inventory", &aspire.AddDatabaseOptions{
		DatabaseName: aspire.StringPtr("inventorydb"),
	})

	oracle.WithDataVolume()

	oracle2.WithDataVolume(&aspire.WithDataVolumeOptions{Name: aspire.StringPtr("oracle-data")})

	oracle2.WithDataBindMount("./oracle-data")

	oracle2.WithInitFiles("./init-scripts")

	oracle2.WithDbSetupBindMount("./setup-scripts")

	otherOracle := builder.AddOracle("other-oracle")
	otherDb := otherOracle.AddDatabase("otherdb")
	oracle.WithReference(otherDb)
	oracle.WithReference(otherDb, &aspire.WithReferenceOptions{
		ConnectionName: aspire.StringPtr("secondary-db"),
	})
	oracle.WithReference(otherOracle)

	oracle3 := builder.AddOracle("oracledb3")
	oracle3.WithLifetime(aspire.ContainerLifetimePersistent)
	oracle3.WithDataVolume(&aspire.WithDataVolumeOptions{Name: aspire.StringPtr("oracle3-data")})
	oracle3.AddDatabase("chaineddb")
	if err = oracle3.Err(); err != nil {
		log.Fatalf(aspire.FormatError(err))
	}

	_ = oracle.PrimaryEndpoint()
	_ = oracle.Host()
	_ = oracle.Port()
	_ = oracle.UserNameReference()
	_ = oracle.UriExpression()
	_ = oracle.JdbcConnectionString()
	_ = oracle.ConnectionStringExpression()

	_, _ = db.DatabaseName()
	_ = db.UriExpression()
	_ = db.JdbcConnectionString()
	_ = db.Parent()
	_ = db.ConnectionStringExpression()

	app, err := builder.Build()
	if err != nil {
		log.Fatalf(aspire.FormatError(err))
	}
	if err := app.Run(nil); err != nil {
		log.Fatalf(aspire.FormatError(err))
	}
}
