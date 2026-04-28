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

	webpubsub := builder.AddAzureWebPubSub("webpubsub")
	if webpubsub.Err() != nil {
		log.Fatalf(aspire.FormatError(webpubsub.Err()))
	}

	hub := webpubsub.AddHub("myhub")
	if hub.Err() != nil {
		log.Fatalf(aspire.FormatError(hub.Err()))
	}

	_ = webpubsub.AddHub("hub2", &aspire.AddHubOptions{HubName: aspire.StringPtr("customhub")})

	hub.AddEventHandler(aspire.RefExpr("https://example.com/handler"))
	hub.AddEventHandler(
		aspire.RefExpr("https://example.com/handler2"),
		&aspire.AddEventHandlerOptions{
			UserEventPattern: aspire.StringPtr("event1"),
			SystemEvents:     []string{"connect", "connected"},
		},
	)

	container := builder.AddContainer("mycontainer", "mcr.microsoft.com/dotnet/samples:aspnetapp")
	if container.Err() != nil {
		log.Fatalf(aspire.FormatError(container.Err()))
	}

	container.WithRoleAssignments(webpubsub, []aspire.AzureWebPubSubRole{
		aspire.AzureWebPubSubRoleWebPubSubServiceOwner,
		aspire.AzureWebPubSubRoleWebPubSubServiceReader,
		aspire.AzureWebPubSubRoleWebPubSubContributor,
	})

	webpubsub.WithRoleAssignments(webpubsub, []aspire.AzureWebPubSubRole{
		aspire.AzureWebPubSubRoleWebPubSubServiceReader,
	})

	container.WithReference(webpubsub)
	container.WithReference(hub)

	if hub.Err() != nil {
		log.Fatalf(aspire.FormatError(hub.Err()))
	}
	if container.Err() != nil {
		log.Fatalf(aspire.FormatError(container.Err()))
	}
	if webpubsub.Err() != nil {
		log.Fatalf(aspire.FormatError(webpubsub.Err()))
	}

	app, err := builder.Build()
	if err != nil {
		log.Fatalf(aspire.FormatError(err))
	}
	if err := app.Run(nil); err != nil {
		log.Fatalf(aspire.FormatError(err))
	}
}
