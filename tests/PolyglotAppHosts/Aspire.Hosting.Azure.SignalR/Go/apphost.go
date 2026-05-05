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

	signalr := builder.AddAzureSignalR("signalr")
	if signalr.Err() != nil {
		log.Fatalf(aspire.FormatError(signalr.Err()))
	}
	signalr.RunAsEmulator()
	if signalr.Err() != nil {
		log.Fatalf(aspire.FormatError(signalr.Err()))
	}

	app, err := builder.Build()
	if err != nil {
		log.Fatalf(aspire.FormatError(err))
	}
	if err := app.Run(nil); err != nil {
		log.Fatalf(aspire.FormatError(err))
	}
}
