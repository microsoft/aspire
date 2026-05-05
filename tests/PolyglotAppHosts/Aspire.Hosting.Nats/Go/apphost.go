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

	nats := builder.AddNats("messaging")
	nats.WithJetStream()
	nats.WithDataVolume()
	if err = nats.Err(); err != nil {
		log.Fatalf(aspire.FormatError(err))
	}

	nats2 := builder.AddNats("messaging2", &aspire.AddNatsOptions{Port: aspire.Float64Ptr(4223)})
	nats2.WithJetStream()
	nats2.WithDataVolume(&aspire.WithDataVolumeOptions{
		Name:       aspire.StringPtr("nats-data"),
		IsReadOnly: aspire.BoolPtr(false),
	})
	nats2.WithLifetime(aspire.ContainerLifetimePersistent)
	if err = nats2.Err(); err != nil {
		log.Fatalf(aspire.FormatError(err))
	}

	nats3 := builder.AddNats("messaging3")
	nats3.WithDataBindMount("/tmp/nats-data")
	if err = nats3.Err(); err != nil {
		log.Fatalf(aspire.FormatError(err))
	}

	customUser := builder.AddParameter("nats-user")
	customPass := builder.AddParameter("nats-pass", &aspire.AddParameterOptions{Secret: aspire.BoolPtr(true)})
	nats4 := builder.AddNats("messaging4", &aspire.AddNatsOptions{
		UserName: &customUser,
		Password: &customPass,
	})
	if err = nats4.Err(); err != nil {
		log.Fatalf(aspire.FormatError(err))
	}

	consumer := builder.AddContainer("consumer", "myimage")
	consumer.WithReference(nats)
	consumer.WithReference(nats4, &aspire.WithReferenceOptions{
		ConnectionName: aspire.StringPtr("messaging4-connection"),
	})
	if err = consumer.Err(); err != nil {
		log.Fatalf(aspire.FormatError(err))
	}

	_ = nats.PrimaryEndpoint()
	_ = nats.Host()
	_ = nats.Port()
	_ = nats.UriExpression()
	_ = nats.UserNameReference()
	_ = nats.ConnectionStringExpression()

	app, err := builder.Build()
	if err != nil {
		log.Fatalf(aspire.FormatError(err))
	}
	if err := app.Run(nil); err != nil {
		log.Fatalf(aspire.FormatError(err))
	}
}
