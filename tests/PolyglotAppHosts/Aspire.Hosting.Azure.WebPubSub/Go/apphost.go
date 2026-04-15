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

	// ── 1. addAzureWebPubSub ──────────────────────────────────────────────────
	webpubsub := builder.AddAzureWebPubSub("webpubsub")

	// ── 2. addHub — simple (no options) ──────────────────────────────────────
	hub := webpubsub.AddHub("myhub")

	// ── 3. addHub — with hubName option ──────────────────────────────────────
	_ = webpubsub.AddHubWithOpts("hub2",
		&aspire.AddHubOptions{HubName: aspire.StringPtr("customhub")})

	// ── 4. addEventHandler — simple (no options) ──────────────────────────────
	hub.AddEventHandler(aspire.RefExpr("https://example.com/handler"))

	// ── 5. addEventHandler — with userEventPattern and systemEvents ───────────
	hub.AddEventHandlerWithOpts(
		aspire.RefExpr("https://example.com/handler2"),
		&aspire.AddEventHandlerOptions{
			UserEventPattern: aspire.StringPtr("event1"),
			SystemEvents:     []string{"connect", "connected"},
		},
	)

	// ── 6. container with role assignments and references ─────────────────────
	container := builder.AddContainer("mycontainer", "mcr.microsoft.com/dotnet/samples:aspnetapp")
	container.WithWebPubSubRoleAssignments(webpubsub, []aspire.AzureWebPubSubRole{
		aspire.AzureWebPubSubRoleWebPubSubServiceOwner,
		aspire.AzureWebPubSubRoleWebPubSubServiceReader,
		aspire.AzureWebPubSubRoleWebPubSubContributor,
	})

	// ── 7. withWebPubSubRoleAssignments on the WebPubSub resource itself ──────
	webpubsub.WithWebPubSubRoleAssignments(webpubsub, []aspire.AzureWebPubSubRole{
		aspire.AzureWebPubSubRoleWebPubSubServiceReader,
	})

	// ── 8. withReference — pass as IResource via handle conversion ────────────
	container.WithReference(aspire.NewIResource(webpubsub.Handle(), webpubsub.Client()))
	container.WithReference(aspire.NewIResource(hub.Handle(), hub.Client()))

	if err = hub.Err(); err != nil {
		log.Fatalf("hub: %v", err)
	}
	if err = container.Err(); err != nil {
		log.Fatalf("container: %v", err)
	}
	if err = webpubsub.Err(); err != nil {
		log.Fatalf("webpubsub: %v", err)
	}

	app, err := builder.Build()
	if err != nil {
		log.Fatalf("Build: %v", err)
	}
	if err := app.Run(nil); err != nil {
		log.Fatalf("Run: %v", err)
	}
}
