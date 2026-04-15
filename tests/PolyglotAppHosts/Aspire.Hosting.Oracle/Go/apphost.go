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

	// addOracle: factory method with defaults
	oracle := builder.AddOracle("oracledb")
	if err = oracle.Err(); err != nil {
		log.Fatalf("oracle: %v", err)
	}

	// addOracle: with custom password and port
	customPassword := builder.AddParameterWithOpts("oracle-password", &aspire.AddParameterOptions{Secret: aspire.BoolPtr(true)})
	oracle2 := builder.AddOracleWithOpts("oracledb2", &aspire.AddOracleOptions{
		Password: customPassword,
		Port:     aspire.Float64Ptr(1522),
	})
	if err = oracle2.Err(); err != nil {
		log.Fatalf("oracle2: %v", err)
	}

	// addDatabase: child resource with default databaseName
	db := oracle.AddDatabase("mydb")
	if err = db.Err(); err != nil {
		log.Fatalf("db: %v", err)
	}

	// addDatabase: child resource with explicit databaseName
	oracle.AddDatabaseWithOpts("inventory", &aspire.AddDatabaseOptions{
		DatabaseName: aspire.StringPtr("inventorydb"),
	})

	// withDataVolume: data persistence (default name)
	oracle.WithDataVolume()

	// withDataVolume: data persistence (custom name)
	oracle2.WithDataVolumeWithOpts(&aspire.WithDataVolumeOptions{Name: aspire.StringPtr("oracle-data")})

	// withDataBindMount
	oracle2.WithDataBindMount("./oracle-data")

	// withInitFiles
	oracle2.WithInitFiles("./init-scripts")

	// withDbSetupBindMount
	oracle2.WithDbSetupBindMount("./setup-scripts")

	// withReference: connection string reference from another oracle resource
	otherOracle := builder.AddOracle("other-oracle")
	otherDb := otherOracle.AddDatabase("otherdb")
	oracle.WithReference(aspire.NewIResource(otherDb.Handle(), otherDb.Client()))
	oracle.WithReferenceWithOpts(aspire.NewIResource(otherDb.Handle(), otherDb.Client()), &aspire.WithReferenceOptions{
		ConnectionName: aspire.StringPtr("secondary-db"),
	})
	oracle.WithReference(aspire.NewIResource(otherOracle.Handle(), otherOracle.Client()))

	// Fluent chaining: multiple methods chained
	oracle3 := builder.AddOracle("oracledb3")
	oracle3.WithLifetime(aspire.ContainerLifetimePersistent)
	oracle3.WithDataVolumeWithOpts(&aspire.WithDataVolumeOptions{Name: aspire.StringPtr("oracle3-data")})
	oracle3.AddDatabase("chaineddb")
	if err = oracle3.Err(); err != nil {
		log.Fatalf("oracle3: %v", err)
	}

	// Property access on OracleDatabaseServerResource
	_, _ = oracle.PrimaryEndpoint()
	_, _ = oracle.Host()
	_, _ = oracle.Port()
	_, _ = oracle.UserNameReference()
	_, _ = oracle.UriExpression()
	_, _ = oracle.JdbcConnectionString()
	_, _ = oracle.ConnectionStringExpression()

	// Property access on OracleDatabaseResource
	_, _ = db.DatabaseName()
	_, _ = db.UriExpression()
	_, _ = db.JdbcConnectionString()
	_, _ = db.Parent()
	_, _ = db.ConnectionStringExpression()

	app, err := builder.Build()
	if err != nil {
		log.Fatalf("Build: %v", err)
	}
	if err := app.Run(nil); err != nil {
		log.Fatalf("Run: %v", err)
	}
}
