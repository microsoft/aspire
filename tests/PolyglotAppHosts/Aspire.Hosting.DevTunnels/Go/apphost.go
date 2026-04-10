// Aspire Go validation AppHost - Aspire.Hosting.DevTunnels
// Mirrors the TypeScript/Python/Java fixture for API surface validation.
// Run `aspire restore --apphost apphost.go` to generate the SDK, then `go build ./...`.
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

	tunnel, err := builder.AddDevTunnel("resource")
	if err != nil {
		log.Fatalf("AddDevTunnel: %v", err)
	}

	tunnel2, err := builder.AddDevTunnel("resource")
	if err != nil {
		log.Fatalf("AddDevTunnel: %v", err)
	}

	_, err = builder.AddDevTunnel("resource")
	if err != nil {
		log.Fatalf("AddDevTunnel: %v", err)
	}

	web, err := builder.AddContainer("resource", "image")
	if err != nil {
		log.Fatalf("AddContainer: %v", err)
	}
	_, _ = web.GetEndpoint("default")
	_, _ = tunnel.WithTunnelReference()

	web2, err := builder.AddContainer("resource", "image")
	if err != nil {
		log.Fatalf("AddContainer: %v", err)
	}
	_, _ = web2.GetEndpoint("default")
	_, _ = tunnel2.WithTunnelReferenceAnonymous()

	tunnel3, err := builder.AddDevTunnel("resource")
	if err != nil {
		log.Fatalf("AddDevTunnel: %v", err)
	}
	_, err = builder.AddContainer("resource", "image")
	if err != nil {
		log.Fatalf("AddContainer: %v", err)
	}
	_, _ = tunnel3.WithTunnelReferenceAll()

	_, err = builder.AddDevTunnel("resource")
	if err != nil {
		log.Fatalf("AddDevTunnel: %v", err)
	}

	_, err = builder.AddDevTunnel("resource")
	if err != nil {
		log.Fatalf("AddDevTunnel: %v", err)
	}

	app, err := builder.Build()
	if err != nil {
		log.Fatalf("Build: %v", err)
	}
	if err := app.Run(nil); err != nil {
		log.Fatalf("Run: %v", err)
	}
}
