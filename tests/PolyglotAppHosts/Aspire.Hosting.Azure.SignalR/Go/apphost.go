package main

import (
	"log"

	"apphost/modules/aspire"
)

func main() {
	builder, err := aspire.CreateBuilder()
	if err != nil {
		log.Fatalf(aspire.FormatError(err))
	}

	signalr := builder.AddAzureSignalR("signalr")
	signalr.ConfigureInfrastructure(func(infrastructure aspire.AzureResourceInfrastructure) {
		service := infrastructure.GetSignalRService()
		if err := service.SetDisableLocalAuth(true).Err(); err != nil {
			log.Fatalf(aspire.FormatError(err))
		}
		if _, err := service.DisableLocalAuth(); err != nil {
			log.Fatalf(aspire.FormatError(err))
		}
	})
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
	if err := app.Run(); err != nil {
		log.Fatalf(aspire.FormatError(err))
	}
}
