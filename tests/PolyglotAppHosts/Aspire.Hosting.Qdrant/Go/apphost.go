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

	// ---- AddQdrant with apiKey and ports ----
	customApiKey := builder.AddParameterWithOpts("qdrant-key", &aspire.AddParameterOptions{Secret: func() *bool { b := true; return &b }()})
	builder.AddQdrantWithOpts("qdrant-custom", &aspire.AddQdrantOptions{
		ApiKey:   customApiKey,
		GrpcPort: func() *float64 { p := float64(16334); return &p }(),
		HttpPort: func() *float64 { p := float64(16333); return &p }(),
	})

	// ---- Simple AddQdrant ----
	qdrant := builder.AddQdrant("qdrant")
	qdrant.WithDataVolumeWithOpts(&aspire.WithDataVolumeOptions{Name: func() *string { s := "qdrant-data"; return &s }()})
	qdrant.WithDataBindMountWithOpts(".", &aspire.WithDataBindMountOptions{IsReadOnly: func() *bool { b := true; return &b }()})
	if err = qdrant.Err(); err != nil {
		log.Fatalf("qdrant: %v", err)
	}

	// ---- WithReference on consumer ----
	consumer := builder.AddContainer("consumer", "busybox")
	consumer.WithReferenceWithOpts(aspire.NewIResource(qdrant.Handle(), qdrant.Client()), &aspire.WithReferenceOptions{ConnectionName: func() *string { s := "qdrant"; return &s }()})
	if err = consumer.Err(); err != nil {
		log.Fatalf("consumer: %v", err)
	}

	// ---- Property access on QdrantServerResource ----
	_, _ = qdrant.PrimaryEndpoint()
	_, _ = qdrant.GrpcHost()
	_, _ = qdrant.GrpcPort()
	_, _ = qdrant.HttpEndpoint()
	_, _ = qdrant.HttpHost()
	_, _ = qdrant.HttpPort()
	_, _ = qdrant.UriExpression()
	_, _ = qdrant.HttpUriExpression()
	_, _ = qdrant.ConnectionStringExpression()

	app, err := builder.Build()
	if err != nil {
		log.Fatalf("Build: %v", err)
	}
	if err := app.Run(nil); err != nil {
		log.Fatalf("Run: %v", err)
	}
}
