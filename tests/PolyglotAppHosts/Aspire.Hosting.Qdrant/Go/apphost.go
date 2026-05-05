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

	customApiKey := builder.AddParameter("qdrant-key", &aspire.AddParameterOptions{Secret: aspire.BoolPtr(true)})
	builder.AddQdrant("qdrant-custom", &aspire.AddQdrantOptions{
		ApiKey:   &customApiKey,
		GrpcPort: aspire.Float64Ptr(16334),
		HttpPort: aspire.Float64Ptr(16333),
	})

	qdrant := builder.AddQdrant("qdrant")
	qdrant.WithDataVolume(&aspire.WithDataVolumeOptions{Name: aspire.StringPtr("qdrant-data")})
	qdrant.WithDataBindMount(".", &aspire.WithDataBindMountOptions{IsReadOnly: aspire.BoolPtr(true)})
	if err = qdrant.Err(); err != nil {
		log.Fatalf(aspire.FormatError(err))
	}

	consumer := builder.AddContainer("consumer", "busybox")
	consumer.WithReference(qdrant, &aspire.WithReferenceOptions{ConnectionName: aspire.StringPtr("qdrant")})
	if err = consumer.Err(); err != nil {
		log.Fatalf(aspire.FormatError(err))
	}

	_ = qdrant.PrimaryEndpoint()
	_ = qdrant.GrpcHost()
	_ = qdrant.GrpcPort()
	_ = qdrant.HttpEndpoint()
	_ = qdrant.HttpHost()
	_ = qdrant.HttpPort()
	_ = qdrant.UriExpression()
	_ = qdrant.HttpUriExpression()
	_ = qdrant.ConnectionStringExpression()

	app, err := builder.Build()
	if err != nil {
		log.Fatalf(aspire.FormatError(err))
	}
	if err := app.Run(nil); err != nil {
		log.Fatalf(aspire.FormatError(err))
	}
}
