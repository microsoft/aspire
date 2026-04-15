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

	// addNats — factory method with default options
	nats := builder.AddNats("messaging")

	// withJetStream — enable JetStream support
	nats.WithJetStream()

	// withDataVolume — add persistent data volume
	nats.WithDataVolume()

	if err = nats.Err(); err != nil {
		log.Fatalf("nats: %v", err)
	}

	// addNats — with port, withJetStream, withDataVolume (named + isReadOnly), withLifetime
	nats2 := builder.AddNatsWithOpts("messaging2", &aspire.AddNatsOptions{Port: aspire.Float64Ptr(4223)})
	nats2.WithJetStream()
	nats2.WithDataVolumeWithOpts(&aspire.WithDataVolumeOptions{
		Name:       aspire.StringPtr("nats-data"),
		IsReadOnly: aspire.BoolPtr(false),
	})
	nats2.WithLifetime(aspire.ContainerLifetimePersistent)
	if err = nats2.Err(); err != nil {
		log.Fatalf("nats2: %v", err)
	}

	// withDataBindMount — bind mount a host directory
	nats3 := builder.AddNats("messaging3")
	nats3.WithDataBindMount("/tmp/nats-data")
	if err = nats3.Err(); err != nil {
		log.Fatalf("nats3: %v", err)
	}

	// addNats — with custom userName and password parameters
	customUser := builder.AddParameter("nats-user")
	customPass := builder.AddParameterWithOpts("nats-pass", &aspire.AddParameterOptions{Secret: aspire.BoolPtr(true)})
	nats4 := builder.AddNatsWithOpts("messaging4", &aspire.AddNatsOptions{
		UserName: customUser,
		Password: customPass,
	})
	if err = nats4.Err(); err != nil {
		log.Fatalf("nats4: %v", err)
	}

	// withReference — a container referencing a NATS resource (connection string)
	consumer := builder.AddContainer("consumer", "myimage")
	consumer.WithReference(aspire.NewIResource(nats.Handle(), nats.Client()))
	consumer.WithReferenceWithOpts(aspire.NewIResource(nats4.Handle(), nats4.Client()), &aspire.WithReferenceOptions{
		ConnectionName: aspire.StringPtr("messaging4-connection"),
	})
	if err = consumer.Err(); err != nil {
		log.Fatalf("consumer: %v", err)
	}

	// Property access on NatsServerResource
	_, _ = nats.PrimaryEndpoint()
	_, _ = nats.Host()
	_, _ = nats.Port()
	_, _ = nats.UriExpression()
	_, _ = nats.UserNameReference()
	_, _ = nats.ConnectionStringExpression()

	app, err := builder.Build()
	if err != nil {
		log.Fatalf("Build: %v", err)
	}
	if err := app.Run(nil); err != nil {
		log.Fatalf("Run: %v", err)
	}
}
