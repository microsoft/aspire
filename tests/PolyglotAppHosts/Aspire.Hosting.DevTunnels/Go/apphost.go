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

	tunnel := builder.AddDevTunnel("mytunnel")

	tunnel2 := builder.AddDevTunnel("mytunnel2", &aspire.AddDevTunnelOptions{
		TunnelId: aspire.StringPtr("custom-tunnel-id"),
	})

	builder.AddDevTunnel("anon-tunnel").WithAnonymousAccess()

	web := builder.AddContainer("web", "nginx")
	web.WithHttpEndpoint(&aspire.WithHttpEndpointOptions{Port: aspire.Float64Ptr(80)})

	webEndpoint := web.GetEndpoint("http")
	tunnel.WithTunnelReference(webEndpoint)
	if err = tunnel.Err(); err != nil {
		log.Fatalf(aspire.FormatError(err))
	}

	web2 := builder.AddContainer("web2", "nginx")
	web2.WithHttpEndpoint(&aspire.WithHttpEndpointOptions{Port: aspire.Float64Ptr(8080)})
	web2Endpoint := web2.GetEndpoint("http")
	tunnel2.WithTunnelReferenceAnonymous(web2Endpoint, true)
	if err = tunnel2.Err(); err != nil {
		log.Fatalf(aspire.FormatError(err))
	}

	tunnel3 := builder.AddDevTunnel("all-endpoints-tunnel")
	web3 := builder.AddContainer("web3", "nginx")
	web3.WithHttpEndpoint(&aspire.WithHttpEndpointOptions{Port: aspire.Float64Ptr(80)})
	tunnel3.WithTunnelReferenceAll(web3, false)
	if err = tunnel3.Err(); err != nil {
		log.Fatalf(aspire.FormatError(err))
	}

	web4 := builder.AddContainer("web4", "nginx")
	web4.WithHttpEndpoint(&aspire.WithHttpEndpointOptions{Port: aspire.Float64Ptr(80)})
	web4Endpoint := web4.GetEndpoint("http")
	tunnel4 := builder.AddDevTunnel("get-endpoint-tunnel")
	tunnel4.WithTunnelReference(web4Endpoint)
	_ = tunnel4.GetTunnelEndpoint(web4Endpoint)

	tunnel5 := builder.AddDevTunnel("configured-tunnel", &aspire.AddDevTunnelOptions{
		TunnelId:       aspire.StringPtr("configured-tunnel-id"),
		AllowAnonymous: aspire.BoolPtr(true),
		Description:    aspire.StringPtr("Configured by the polyglot validation app"),
		Labels:         []string{"validation", "polyglot"},
	})
	web5 := builder.AddContainer("web5", "nginx")
	web5.WithHttpEndpoint(&aspire.WithHttpEndpointOptions{Port: aspire.Float64Ptr(9090)})
	web5Endpoint := web5.GetEndpoint("http")
	tunnel5.WithTunnelReferenceAnonymous(web5Endpoint, true)

	builder.AddDevTunnel("chained-tunnel").WithAnonymousAccess()

	app, err := builder.Build()
	if err != nil {
		log.Fatalf(aspire.FormatError(err))
	}
	if err := app.Run(nil); err != nil {
		log.Fatalf(aspire.FormatError(err))
	}
}
