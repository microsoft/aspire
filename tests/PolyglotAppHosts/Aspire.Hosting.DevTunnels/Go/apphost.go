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

	tunnel, err := builder.AddDevTunnel("resource", nil, nil, nil, nil)
	if err != nil {
		log.Fatalf("AddDevTunnel: %v", err)
	}

	tunnel2, err := builder.AddDevTunnel("resource", nil, nil, nil, nil)
	if err != nil {
		log.Fatalf("AddDevTunnel: %v", err)
	}

	_, err = builder.AddDevTunnel("resource", nil, nil, nil, nil)
	if err != nil {
		log.Fatalf("AddDevTunnel: %v", err)
	}

	web, err := builder.AddContainer("resource", "image")
	if err != nil {
		log.Fatalf("AddContainer: %v", err)
	}
	endpoint, err := web.GetEndpoint("default")
	if err != nil {
		log.Fatalf("GetEndpoint: %v", err)
	}
	tunnel.WithTunnelReference(endpoint)

	web2, err := builder.AddContainer("resource", "image")
	if err != nil {
		log.Fatalf("AddContainer: %v", err)
	}
	endpoint2, err := web2.GetEndpoint("default")
	if err != nil {
		log.Fatalf("GetEndpoint: %v", err)
	}
	tunnel2.WithTunnelReferenceAnonymous(endpoint2, false)

	tunnel3, err := builder.AddDevTunnel("resource", nil, nil, nil, nil)
	if err != nil {
		log.Fatalf("AddDevTunnel: %v", err)
	}
	_, err = builder.AddContainer("resource", "image")
	if err != nil {
		log.Fatalf("AddContainer: %v", err)
	}
	tunnel3.WithTunnelReferenceAll(nil, false)

	_, err = builder.AddDevTunnel("resource", nil, nil, nil, nil)
	if err != nil {
		log.Fatalf("AddDevTunnel: %v", err)
	}

	_, err = builder.AddDevTunnel("resource", nil, nil, nil, nil)
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
