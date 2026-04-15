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

	// 1. addMilvus: basic Milvus server resource
	milvus := builder.AddMilvus("milvus")

	// 2. addMilvus: with custom apiKey parameter
	customKey := builder.AddParameterWithOpts("milvus-key", &aspire.AddParameterOptions{Secret: aspire.BoolPtr(true)})
	milvus2 := builder.AddMilvusWithOpts("milvus2", &aspire.AddMilvusOptions{ApiKey: customKey})

	// 3. addMilvus: with explicit gRPC port
	builder.AddMilvusWithOpts("milvus3", &aspire.AddMilvusOptions{GrpcPort: aspire.Float64Ptr(19531)})

	// 4. addDatabase: add database to Milvus server
	db := milvus.AddDatabase("mydb")
	if err = db.Err(); err != nil {
		log.Fatalf("db: %v", err)
	}

	// 5. addDatabase: with custom database name
	milvus.AddDatabaseWithOpts("db2", &aspire.AddDatabaseOptions{DatabaseName: aspire.StringPtr("customdb")})

	// 6. withAttu: add Attu administration tool (no options)
	milvus.WithAttu(nil)

	// 7. withAttu: with container name
	milvus2.WithAttuWithOpts(&aspire.WithAttuOptions{ContainerName: aspire.StringPtr("my-attu")}, nil)

	// 8. withAttu: with configureContainer callback
	builder.AddMilvus("milvus-attu-cfg").WithAttu(func(container *aspire.AttuResource) {
		container.WithHttpEndpointWithOpts(&aspire.WithHttpEndpointOptions{Port: aspire.Float64Ptr(3001)})
	})

	// 9. withDataVolume: persistent data volume
	milvus.WithDataVolume()

	// 10. withDataVolume: with custom name
	milvus2.WithDataVolumeWithOpts(&aspire.WithDataVolumeOptions{Name: aspire.StringPtr("milvus-data")})

	// 11. withDataBindMount: bind mount for data
	builder.AddMilvus("milvus-bind").WithDataBindMount("./milvus-data")

	// 12. withDataBindMount: with read-only flag
	builder.AddMilvus("milvus-bind-ro").WithDataBindMountWithOpts("./milvus-data-ro", &aspire.WithDataBindMountOptions{IsReadOnly: aspire.BoolPtr(true)})

	// 13. withConfigurationFile: custom milvus.yaml
	builder.AddMilvus("milvus-cfg").WithConfigurationFile("./milvus.yaml")

	// 14. Fluent chaining: multiple With* methods
	milvusChained := builder.AddMilvus("milvus-chained")
	milvusChained.WithLifetime(aspire.ContainerLifetimePersistent)
	milvusChained.WithDataVolumeWithOpts(&aspire.WithDataVolumeOptions{Name: aspire.StringPtr("milvus-chained-data")})
	milvusChained.WithAttu(nil)

	// 15. withReference: use Milvus database from a container resource
	api := builder.AddContainer("api", "myregistry/myapp")
	api.WithReference(aspire.NewIResource(db.Handle(), db.Client()))

	// 16. withReference: use Milvus server directly
	api.WithReference(aspire.NewIResource(milvus.Handle(), milvus.Client()))
	if err = api.Err(); err != nil {
		log.Fatalf("api: %v", err)
	}

	// Property access on MilvusServerResource
	_, _ = milvus.PrimaryEndpoint()
	_, _ = milvus.Host()
	_, _ = milvus.Port()
	_, _ = milvus.Token()
	_, _ = milvus.UriExpression()
	_, _ = milvus.ConnectionStringExpression()
	_ = milvus.Databases()

	if err = milvus.Err(); err != nil {
		log.Fatalf("milvus: %v", err)
	}

	app, err := builder.Build()
	if err != nil {
		log.Fatalf("Build: %v", err)
	}
	if err := app.Run(nil); err != nil {
		log.Fatalf("Run: %v", err)
	}
}
