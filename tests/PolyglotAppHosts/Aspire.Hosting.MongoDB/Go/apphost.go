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

	// Test 1: Basic MongoDB resource creation (addMongoDB)
	mongo := builder.AddMongoDB("mongo")

	// Test 2: Add database to MongoDB (addDatabase)
	mongo.AddDatabase("mydb")

	// Test 3: Add database with custom database name
	mongo.AddDatabaseWithOpts("db2", &aspire.AddDatabaseOptions{DatabaseName: aspire.StringPtr("customdb2")})

	if err = mongo.Err(); err != nil {
		log.Fatalf("mongo: %v", err)
	}

	// Test 4: Test withDataVolume
	builder.AddMongoDB("mongo-volume").WithDataVolume()

	// Test 5: Test withDataVolume with custom name
	builder.AddMongoDB("mongo-volume-named").WithDataVolumeWithOpts(&aspire.WithDataVolumeOptions{Name: aspire.StringPtr("mongo-data")})

	// Test 6: Test withHostPort on MongoExpress
	builder.AddMongoDB("mongo-express").WithMongoExpress(func(container *aspire.MongoExpressContainerResource) {
		container.WithHostPort(8082)
	})

	// Test 7: Test withMongoExpress with container name
	builder.AddMongoDB("mongo-express-named").WithMongoExpressWithOpts(
		&aspire.WithMongoExpressOptions{ContainerName: aspire.StringPtr("my-mongo-express")},
		nil,
	)

	// Test 8: Custom password parameter with addParameter
	customPassword := builder.AddParameterWithOpts("mongo-password", &aspire.AddParameterOptions{Secret: aspire.BoolPtr(true)})
	builder.AddMongoDBWithOpts("mongo-custom-pass", &aspire.AddMongoDBOptions{Password: customPassword})

	// Test 9: Chained configuration - multiple With* methods
	mongoChained := builder.AddMongoDB("mongo-chained")
	mongoChained.WithLifetime(aspire.ContainerLifetimePersistent)
	mongoChained.WithDataVolumeWithOpts(&aspire.WithDataVolumeOptions{Name: aspire.StringPtr("mongo-chained-data")})

	// Test 10: Add multiple databases to same server
	mongoChained.AddDatabase("app-db")
	mongoChained.AddDatabaseWithOpts("analytics-db", &aspire.AddDatabaseOptions{DatabaseName: aspire.StringPtr("analytics")})

	if err = mongoChained.Err(); err != nil {
		log.Fatalf("mongoChained: %v", err)
	}

	// Property access on MongoDBServerResource
	_, _ = mongo.PrimaryEndpoint()
	_, _ = mongo.Host()
	_, _ = mongo.Port()
	_, _ = mongo.UriExpression()
	_, _ = mongo.UserNameReference()
	_, _ = mongo.ConnectionStringExpression()
	_ = mongo.Databases()

	app, err := builder.Build()
	if err != nil {
		log.Fatalf("Build: %v", err)
	}
	if err := app.Run(nil); err != nil {
		log.Fatalf("Run: %v", err)
	}
}
