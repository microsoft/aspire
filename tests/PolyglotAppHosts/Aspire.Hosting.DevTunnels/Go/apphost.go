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

	// ── 1. addDevTunnel (simple) ──────────────────────────────────────────────
	tunnel := builder.AddDevTunnel("mytunnel")

	// ── 2. addDevTunnelWithOpts (tunnelId) ────────────────────────────────────
	tunnel2 := builder.AddDevTunnelWithOpts("mytunnel2", &aspire.AddDevTunnelOptions{
		TunnelId: aspire.StringPtr("custom-tunnel-id"),
	})

	// ── 3. withAnonymousAccess ────────────────────────────────────────────────
	builder.AddDevTunnel("anon-tunnel").WithAnonymousAccess()

	// ── 4. container with endpoint ───────────────────────────────────────────
	web := builder.AddContainer("web", "nginx")
	web.WithHttpEndpointWithOpts(&aspire.WithHttpEndpointOptions{Port: aspire.Float64Ptr(80)})

	// ── 5. withTunnelReference ────────────────────────────────────────────────
	webEndpoint, err := web.GetEndpoint("http")
	if err != nil {
		log.Fatalf("GetEndpoint: %v", err)
	}
	tunnel.WithTunnelReference(webEndpoint)
	if err = tunnel.Err(); err != nil {
		log.Fatalf("tunnel: %v", err)
	}

	// ── 6. withTunnelReferenceAnonymous ───────────────────────────────────────
	web2 := builder.AddContainer("web2", "nginx")
	web2.WithHttpEndpointWithOpts(&aspire.WithHttpEndpointOptions{Port: aspire.Float64Ptr(8080)})
	web2Endpoint, err := web2.GetEndpoint("http")
	if err != nil {
		log.Fatalf("GetEndpoint web2: %v", err)
	}
	tunnel2.WithTunnelReferenceAnonymous(web2Endpoint, true)
	if err = tunnel2.Err(); err != nil {
		log.Fatalf("tunnel2: %v", err)
	}

	// ── 7. withTunnelReferenceAll ─────────────────────────────────────────────
	tunnel3 := builder.AddDevTunnel("all-endpoints-tunnel")
	web3 := builder.AddContainer("web3", "nginx")
	web3.WithHttpEndpointWithOpts(&aspire.WithHttpEndpointOptions{Port: aspire.Float64Ptr(80)})
	tunnel3.WithTunnelReferenceAll(aspire.NewIResourceWithEndpoints(web3.Handle(), web3.Client()), false)
	if err = tunnel3.Err(); err != nil {
		log.Fatalf("tunnel3: %v", err)
	}

	// ── 8. getTunnelEndpoint ──────────────────────────────────────────────────
	web4 := builder.AddContainer("web4", "nginx")
	web4.WithHttpEndpointWithOpts(&aspire.WithHttpEndpointOptions{Port: aspire.Float64Ptr(80)})
	web4Endpoint, err := web4.GetEndpoint("http")
	if err != nil {
		log.Fatalf("GetEndpoint web4: %v", err)
	}
	tunnel4 := builder.AddDevTunnel("get-endpoint-tunnel")
	tunnel4.WithTunnelReference(web4Endpoint)
	_, _ = tunnel4.GetTunnelEndpoint(web4Endpoint)

	// ── 9. addDevTunnelWithOpts (all options) ─────────────────────────────────
	tunnel5 := builder.AddDevTunnelWithOpts("configured-tunnel", &aspire.AddDevTunnelOptions{
		TunnelId:        aspire.StringPtr("configured-tunnel-id"),
		AllowAnonymous:  aspire.BoolPtr(true),
		Description:     aspire.StringPtr("Configured by the polyglot validation app"),
		Labels:          []string{"validation", "polyglot"},
	})
	web5 := builder.AddContainer("web5", "nginx")
	web5.WithHttpEndpointWithOpts(&aspire.WithHttpEndpointOptions{Port: aspire.Float64Ptr(9090)})
	web5Endpoint, err := web5.GetEndpoint("http")
	if err != nil {
		log.Fatalf("GetEndpoint web5: %v", err)
	}
	tunnel5.WithTunnelReferenceAnonymous(web5Endpoint, true)

	// ── 10. chained configuration ─────────────────────────────────────────────
	builder.AddDevTunnel("chained-tunnel").WithAnonymousAccess()

	app, err := builder.Build()
	if err != nil {
		log.Fatalf("Build: %v", err)
	}
	if err := app.Run(nil); err != nil {
		log.Fatalf("Run: %v", err)
	}
}
