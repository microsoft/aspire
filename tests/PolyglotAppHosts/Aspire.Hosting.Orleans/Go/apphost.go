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

	provider := builder.AddConnectionString("provider", &aspire.AddConnectionStringOptions{
		EnvironmentVariableNameOrExpression: "ORLEANS_PROVIDER_CONNECTION_STRING",
	})

	orleans := builder.AddOrleans("orleans").
		WithClusterId("cluster-id").
		WithServiceId("service-id").
		WithClustering(provider).
		WithDevelopmentClustering().
		WithGrainStorage("grain-storage", provider).
		WithMemoryGrainStorage("memory-grain-storage").
		WithStreaming("streaming", provider).
		WithMemoryStreaming("memory-streaming").
		WithBroadcastChannel("broadcast").
		WithReminders(provider).
		WithMemoryReminders().
		WithGrainDirectory("grain-directory", provider)

	orleansClient := orleans.AsClient()

	silo := builder.AddContainer("silo", "redis")
	silo.WithReference(orleansClient)
	if err = silo.Err(); err != nil {
		log.Fatalf(aspire.FormatError(err))
	}

	client := builder.AddContainer("client", "redis")
	client.WithReference(orleansClient)
	if err = client.Err(); err != nil {
		log.Fatalf(aspire.FormatError(err))
	}

	app, err := builder.Build()
	if err != nil {
		log.Fatalf(aspire.FormatError(err))
	}
	if err := app.Run(nil); err != nil {
		log.Fatalf(aspire.FormatError(err))
	}
}
