package main

import (
	"log"

	"apphost/modules/aspire"
)

func main() {
	builder, err := aspire.CreateBuilder()
	if err != nil {
		log.Fatalf(aspire.FormatError(err))
	}

	mongo := builder.AddMongoDB("mongo")

	mongo.AddDatabase("mydb")

	mongo.AddDatabase("db2", &aspire.AddDatabaseOptions{DatabaseName: aspire.StringPtr("customdb2")})

	if err = mongo.Err(); err != nil {
		log.Fatalf(aspire.FormatError(err))
	}

	builder.AddMongoDB("mongo-volume").WithDataVolume()

	builder.AddMongoDB("mongo-volume-named").WithDataVolume(&aspire.WithDataVolumeOptions{Name: aspire.StringPtr("mongo-data")})

	builder.AddMongoDB("mongo-express").WithMongoExpress(&aspire.WithMongoExpressOptions{
		ConfigureContainer: func(container aspire.MongoExpressContainerResource) {
			container.WithHostPort(8082)
		},
	})

	builder.AddMongoDB("mongo-express-named").WithMongoExpress(&aspire.WithMongoExpressOptions{
		ContainerName: aspire.StringPtr("my-mongo-express"),
	})

	customPassword := builder.AddParameter("mongo-password", &aspire.AddParameterOptions{Secret: aspire.BoolPtr(true)})
	builder.AddMongoDB("mongo-custom-pass", &aspire.AddMongoDBOptions{Password: &customPassword})

	mongoChained := builder.AddMongoDB("mongo-chained")
	mongoChained.WithPersistentLifetime()
	mongoChained.WithDataVolume(&aspire.WithDataVolumeOptions{Name: aspire.StringPtr("mongo-chained-data")})

	mongoChained.AddDatabase("app-db")
	mongoChained.AddDatabase("analytics-db", &aspire.AddDatabaseOptions{DatabaseName: aspire.StringPtr("analytics")})

	if err = mongoChained.Err(); err != nil {
		log.Fatalf(aspire.FormatError(err))
	}

	// Test 11: Test WithBindIpAll
	builder.AddMongoDB("mongo-bind-all").WithBindIpAll()

	// Test 12: Test WithReplicaSet
	mongoRs := builder.AddMongoDB("mongo-rs").WithReplicaSet("rs0")
	if err = mongoRs.Err(); err != nil {
		log.Fatalf(aspire.FormatError(err))
	}

	// Test 13: Test WithTls with default mode
	builder.AddMongoDB("mongo-tls").WithTls()

	// Test 14: Test WithTls with specific mode
	builder.AddMongoDB("mongo-tls-allow").WithTls(&aspire.WithTlsOptions{Mode: aspire.StringPtr("allowTls")})

	// Test 15: Test WithKeyFile for replica set member
	keyFileParam := builder.AddParameter("rs-keyfile", &aspire.AddParameterOptions{Secret: aspire.BoolPtr(true), Value: aspire.StringPtr("my-secret-key")})
	builder.AddMongoDB("mongo-rs-secured").WithReplicaSet("rs-secure").WithKeyFile(keyFileParam, "/etc/rs.key")

	// Test 16: Complete replica set with security - TLS + KeyFile + ReplicaSet
	tlsKeyFileParam := builder.AddParameter("rs-tls-key", &aspire.AddParameterOptions{Secret: aspire.BoolPtr(true), Value: aspire.StringPtr("tls-secret")})
	mongoRsFull := builder.AddMongoDB("mongo-rs-full").WithReplicaSet("rs-full").WithKeyFile(tlsKeyFileParam, "/etc/rs.key").WithTls(&aspire.WithTlsOptions{Mode: aspire.StringPtr("requireTls")})
	if err = mongoRsFull.Err(); err != nil {
		log.Fatalf(aspire.FormatError(err))
	}

	_ = mongo.PrimaryEndpoint()
	_ = mongo.Host()
	_ = mongo.Port()
	_ = mongo.UriExpression()
	_ = mongo.UserNameReference()
	_ = mongo.ConnectionStringExpression()
	_, _ = mongo.Databases()

	app, err := builder.Build()
	if err != nil {
		log.Fatalf(aspire.FormatError(err))
	}
	if err := app.Run(); err != nil {
		log.Fatalf(aspire.FormatError(err))
	}
}
