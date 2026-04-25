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

	password := builder.AddParameter("valkey-password", &aspire.AddParameterOptions{Secret: aspire.BoolPtr(true)})
	valkey := builder.AddValkey("cache", &aspire.AddValkeyOptions{
		Port:     aspire.Float64Ptr(6380),
		Password: &password,
	})

	valkey.WithDataVolume(&aspire.WithDataVolumeOptions{Name: aspire.StringPtr("valkey-data")})
	valkey.WithDataBindMount(".", &aspire.WithDataBindMountOptions{IsReadOnly: aspire.BoolPtr(true)})
	valkey.WithPersistence(&aspire.WithPersistenceOptions{
		Interval:             aspire.Float64Ptr(100000000),
		KeysChangedThreshold: aspire.Float64Ptr(1),
	})
	if err = valkey.Err(); err != nil {
		log.Fatalf(aspire.FormatError(err))
	}

	_ = valkey.PrimaryEndpoint()
	_ = valkey.Host()
	_ = valkey.Port()
	_ = valkey.UriExpression()
	_ = valkey.ConnectionStringExpression()

	app, err := builder.Build()
	if err != nil {
		log.Fatalf(aspire.FormatError(err))
	}
	if err := app.Run(nil); err != nil {
		log.Fatalf(aspire.FormatError(err))
	}
}
