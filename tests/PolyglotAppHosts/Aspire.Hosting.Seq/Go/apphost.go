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

	// ---- AddSeq with admin password parameter ----
	adminPassword := builder.AddParameterWithOpts("seq-admin-password", &aspire.AddParameterOptions{Secret: func() *bool { b := true; return &b }()})
	seq := builder.AddSeqWithOpts("seq", adminPassword, &aspire.AddSeqOptions{Port: func() *float64 { p := float64(5341); return &p }()})

	// ---- WithDataVolume ----
	seq.WithDataVolume()
	seq.WithDataVolumeWithOpts(&aspire.WithDataVolumeOptions{Name: func() *string { s := "seq-data"; return &s }(), IsReadOnly: func() *bool { b := false; return &b }()})

	// ---- WithDataBindMount ----
	seq.WithDataBindMountWithOpts("./seq-data", &aspire.WithDataBindMountOptions{IsReadOnly: func() *bool { b := true; return &b }()})

	if err = seq.Err(); err != nil {
		log.Fatalf("seq: %v", err)
	}

	// ---- Property access on SeqResource ----
	_, _ = seq.PrimaryEndpoint()
	_, _ = seq.Host()
	_, _ = seq.Port()
	_, _ = seq.UriExpression()
	_, _ = seq.ConnectionStringExpression()

	app, err := builder.Build()
	if err != nil {
		log.Fatalf("Build: %v", err)
	}
	if err := app.Run(nil); err != nil {
		log.Fatalf("Run: %v", err)
	}
}
