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

	// Test 12: Test WithReplicaSet with WithKeyFile and WithTls
	keyFileParam := builder.AddParameter("rs-keyfile", &aspire.AddParameterOptions{Secret: aspire.BoolPtr(true), Value: aspire.StringPtr("my-secret-key")})
	mongoRsMember := builder.AddMongoDB("mongo-rs-member").WithReplicaSet("rs0").WithKeyFile(keyFileParam, "/etc/rs.key").WithTls()
	if err = mongoRsMember.Err(); err != nil {
		log.Fatalf(aspire.FormatError(err))
	}

	// Test 13: Test AddMongoDBReplicaSet with WithMember
	rsKeyFileParam := builder.AddParameter("rs-shared-key", &aspire.AddParameterOptions{Secret: aspire.BoolPtr(true), Value: aspire.StringPtr("replica-set-key")})
	mongo1 := builder.AddMongoDB("mongo-rs-1").WithKeyFile(rsKeyFileParam, "/etc/rs.key").WithTls()
	mongo2 := builder.AddMongoDB("mongo-rs-2").WithKeyFile(rsKeyFileParam, "/etc/rs.key").WithTls()
	
	replicaSet := builder.AddMongoDBReplicaSet("rs0").WithMember(mongo1).WithMember(mongo2)
	if err = replicaSet.Err(); err != nil {
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
