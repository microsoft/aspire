package main

import (
	"log"

	"apphost/modules/aspire"
)

func main() {
	builder, err := aspire.CreateBuilder()
	if err != nil {
		log.Fatalf("CreateBuilder: %v", err)
	}

	garnet := builder.AddGarnet("cache")
	_, _ = garnet.PrimaryEndpoint()
	_, _ = garnet.Host()
	_, _ = garnet.Port()
	_, _ = garnet.UriExpression()
	_, _ = garnet.ConnectionStringExpression()
	if err = garnet.Err(); err != nil {
		log.Fatalf("garnet: %v", err)
	}

	app, err := builder.Build()
	if err != nil {
		log.Fatalf("Build: %v", err)
	}
	if err := app.Run(); err != nil {
		log.Fatalf("Run: %v", err)
	}
}
