package main

import (
	"log"

	"apphost/modules/aspire"
)

func main() {
	builder, err := aspire.CreateBuilder()
	if err != nil {
		log.Fatalf("CreateBuilder: %v", err)
	}

	// ---- AddValkey with password and port ----
	password := builder.AddParameter("valkey-password", &aspire.AddParameterOptions{Secret: aspire.BoolPtr(true)})
	valkey := builder.AddValkey("cache", &aspire.AddValkeyOptions{
		Port:     aspire.Float64Ptr(6380),
		Password: password,
	})

	// ---- Fluent chaining ----
	valkey.WithDataVolume(&aspire.WithDataVolumeOptions{Name: aspire.StringPtr("valkey-data")})
	valkey.WithDataBindMount(".", &aspire.WithDataBindMountOptions{IsReadOnly: aspire.BoolPtr(true)})
	valkey.WithPersistence(&aspire.WithPersistenceOptions{
		Interval:             aspire.Float64Ptr(100000000),
		KeysChangedThreshold: aspire.Float64Ptr(1),
	})
	if err = valkey.Err(); err != nil {
		log.Fatalf("valkey: %v", err)
	}

	// ---- Property access on ValkeyResource ----
	_, _ = valkey.PrimaryEndpoint()
	_, _ = valkey.Host()
	_, _ = valkey.Port()
	_, _ = valkey.UriExpression()
	_, _ = valkey.ConnectionStringExpression()

	app, err := builder.Build()
	if err != nil {
		log.Fatalf("Build: %v", err)
	}
	if err := app.Run(); err != nil {
		log.Fatalf("Run: %v", err)
	}
}
