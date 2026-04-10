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

	tunnel := builder.AddDevTunnel("resource", nil, nil, nil, nil)

	tunnel2 := builder.AddDevTunnel("resource", nil, nil, nil, nil)

	builder.AddDevTunnel("resource", nil, nil, nil, nil)

	web := builder.AddContainer("resource", "image")
	endpoint, err := web.GetEndpoint("default")
	if err != nil {
		log.Fatalf("GetEndpoint: %v", err)
	}
	tunnel.WithTunnelReference(endpoint)
	if err = tunnel.Err(); err != nil {
		log.Fatalf("tunnel: %v", err)
	}

	web2 := builder.AddContainer("resource", "image")
	endpoint2, err := web2.GetEndpoint("default")
	if err != nil {
		log.Fatalf("GetEndpoint: %v", err)
	}
	tunnel2.WithTunnelReferenceAnonymous(endpoint2, false)
	if err = tunnel2.Err(); err != nil {
		log.Fatalf("tunnel2: %v", err)
	}

	tunnel3 := builder.AddDevTunnel("resource", nil, nil, nil, nil)
	builder.AddContainer("resource", "image")
	tunnel3.WithTunnelReferenceAll(nil, false)
	if err = tunnel3.Err(); err != nil {
		log.Fatalf("tunnel3: %v", err)
	}

	builder.AddDevTunnel("resource", nil, nil, nil, nil)

	builder.AddDevTunnel("resource", nil, nil, nil, nil)

	app, err := builder.Build()
	if err != nil {
		log.Fatalf("Build: %v", err)
	}
	if err := app.Run(nil); err != nil {
		log.Fatalf("Run: %v", err)
	}
}
