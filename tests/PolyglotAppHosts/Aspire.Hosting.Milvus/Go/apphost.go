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

	milvus := builder.AddMilvus("milvus")

	customKey := builder.AddParameter("milvus-key", &aspire.AddParameterOptions{Secret: aspire.BoolPtr(true)})
	milvus2 := builder.AddMilvus("milvus2", &aspire.AddMilvusOptions{ApiKey: &customKey})

	builder.AddMilvus("milvus3", &aspire.AddMilvusOptions{GrpcPort: aspire.Float64Ptr(19531)})

	db := milvus.AddDatabase("mydb")
	if err = db.Err(); err != nil {
		log.Fatalf(aspire.FormatError(err))
	}

	milvus.AddDatabase("db2", &aspire.AddDatabaseOptions{DatabaseName: aspire.StringPtr("customdb")})

	milvus.WithAttu()

	milvus2.WithAttu(&aspire.WithAttuOptions{ContainerName: aspire.StringPtr("my-attu")})

	builder.AddMilvus("milvus-attu-cfg").WithAttu(&aspire.WithAttuOptions{
		ConfigureContainer: func(container aspire.AttuResource) {
			container.WithHttpEndpoint(&aspire.WithHttpEndpointOptions{Port: aspire.Float64Ptr(3001)})
		},
	})

	milvus.WithDataVolume()

	milvus2.WithDataVolume(&aspire.WithDataVolumeOptions{Name: aspire.StringPtr("milvus-data")})

	builder.AddMilvus("milvus-bind").WithDataBindMount("./milvus-data")

	builder.AddMilvus("milvus-bind-ro").WithDataBindMount("./milvus-data-ro", &aspire.WithDataBindMountOptions{IsReadOnly: aspire.BoolPtr(true)})

	builder.AddMilvus("milvus-cfg").WithConfigurationFile("./milvus.yaml")

	milvusChained := builder.AddMilvus("milvus-chained")
	milvusChained.WithLifetime(aspire.ContainerLifetimePersistent)
	milvusChained.WithDataVolume(&aspire.WithDataVolumeOptions{Name: aspire.StringPtr("milvus-chained-data")})
	milvusChained.WithAttu()

	api := builder.AddContainer("api", "myregistry/myapp")
	api.WithReference(db)
	api.WithReference(milvus)
	if err = api.Err(); err != nil {
		log.Fatalf(aspire.FormatError(err))
	}

	_ = milvus.PrimaryEndpoint()
	_ = milvus.Host()
	_ = milvus.Port()
	_ = milvus.Token()
	_ = milvus.UriExpression()
	_ = milvus.ConnectionStringExpression()
	_, _ = milvus.Databases()

	if err = milvus.Err(); err != nil {
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
