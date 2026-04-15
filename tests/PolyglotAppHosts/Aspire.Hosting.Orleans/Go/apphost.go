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

	provider := builder.AddConnectionStringWithOpts("provider", &aspire.AddConnectionStringOptions{
		EnvironmentVariableName: aspire.StringPtr("ORLEANS_PROVIDER_CONNECTION_STRING"),
	})

	orleans, err := builder.AddOrleans("orleans")
	if err != nil {
		log.Fatalf("AddOrleans: %v", err)
	}

	if _, err = orleans.WithClusterId("cluster-id"); err != nil {
		log.Fatalf("WithClusterId: %v", err)
	}
	if _, err = orleans.WithServiceId("service-id"); err != nil {
		log.Fatalf("WithServiceId: %v", err)
	}
	if _, err = orleans.WithClustering(provider); err != nil {
		log.Fatalf("WithClustering: %v", err)
	}
	if _, err = orleans.WithDevelopmentClustering(); err != nil {
		log.Fatalf("WithDevelopmentClustering: %v", err)
	}
	if _, err = orleans.WithGrainStorage("grain-storage", provider); err != nil {
		log.Fatalf("WithGrainStorage: %v", err)
	}
	if _, err = orleans.WithMemoryGrainStorage("memory-grain-storage"); err != nil {
		log.Fatalf("WithMemoryGrainStorage: %v", err)
	}
	if _, err = orleans.WithStreaming("streaming", provider); err != nil {
		log.Fatalf("WithStreaming: %v", err)
	}
	if _, err = orleans.WithMemoryStreaming("memory-streaming"); err != nil {
		log.Fatalf("WithMemoryStreaming: %v", err)
	}
	if _, err = orleans.WithBroadcastChannel("broadcast"); err != nil {
		log.Fatalf("WithBroadcastChannel: %v", err)
	}
	if _, err = orleans.WithReminders(provider); err != nil {
		log.Fatalf("WithReminders: %v", err)
	}
	if _, err = orleans.WithMemoryReminders(); err != nil {
		log.Fatalf("WithMemoryReminders: %v", err)
	}
	if _, err = orleans.WithGrainDirectory("grain-directory", provider); err != nil {
		log.Fatalf("WithGrainDirectory: %v", err)
	}

	orleansClient, err := orleans.AsClient()
	if err != nil {
		log.Fatalf("AsClient: %v", err)
	}

	silo := builder.AddContainer("silo", "redis")
	silo.WithOrleansReference(orleans)
	if err = silo.Err(); err != nil {
		log.Fatalf("silo: %v", err)
	}

	client := builder.AddContainer("client", "redis")
	client.WithOrleansClientReference(orleansClient)
	if err = client.Err(); err != nil {
		log.Fatalf("client: %v", err)
	}

	app, err := builder.Build()
	if err != nil {
		log.Fatalf("Build: %v", err)
	}
	if err := app.Run(nil); err != nil {
		log.Fatalf("Run: %v", err)
	}
}
