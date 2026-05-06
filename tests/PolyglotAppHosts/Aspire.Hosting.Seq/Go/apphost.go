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

	adminPassword := builder.AddParameter("seq-admin-password", &aspire.AddParameterOptions{Secret: aspire.BoolPtr(true)})
	seq := builder.AddSeq("seq", adminPassword, &aspire.AddSeqOptions{Port: aspire.Float64Ptr(5341)})

	seq.WithDataVolume()
	seq.WithDataVolume(&aspire.WithDataVolumeOptions{Name: aspire.StringPtr("seq-data"), IsReadOnly: aspire.BoolPtr(false)})
	seq.WithDataBindMount("./seq-data", &aspire.WithDataBindMountOptions{IsReadOnly: aspire.BoolPtr(true)})

	if err = seq.Err(); err != nil {
		log.Fatalf(aspire.FormatError(err))
	}

	_ = seq.PrimaryEndpoint()
	_ = seq.Host()
	_ = seq.Port()
	_ = seq.UriExpression()
	_ = seq.ConnectionStringExpression()

	app, err := builder.Build()
	if err != nil {
		log.Fatalf(aspire.FormatError(err))
	}
	if err := app.Run(nil); err != nil {
		log.Fatalf(aspire.FormatError(err))
	}
}
