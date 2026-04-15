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

	// ---- AddValkey with password and port ----
	password := builder.AddParameterWithOpts("valkey-password", &aspire.AddParameterOptions{Secret: func() *bool { b := true; return &b }()})
	valkey := builder.AddValkeyWithOpts("cache", &aspire.AddValkeyOptions{
		Port:     func() *float64 { p := float64(6380); return &p }(),
		Password: password,
	})

	// ---- Fluent chaining ----
	valkey.WithDataVolumeWithOpts(&aspire.WithDataVolumeOptions{Name: func() *string { s := "valkey-data"; return &s }()})
	valkey.WithDataBindMountWithOpts(".", &aspire.WithDataBindMountOptions{IsReadOnly: func() *bool { b := true; return &b }()})
	valkey.WithPersistenceWithOpts(&aspire.WithPersistenceOptions{
		Interval:            func() *float64 { v := float64(100000000); return &v }(),
		KeysChangedThreshold: func() *float64 { v := float64(1); return &v }(),
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
	if err := app.Run(nil); err != nil {
		log.Fatalf("Run: %v", err)
	}
}
