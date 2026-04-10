// Aspire Go validation AppHost - Aspire.Hosting.Garnet
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

	garnet, err := builder.AddGarnet("resource")
	if err != nil {
		log.Fatalf("AddGarnet: %v", err)
	}
	_, _ = garnet.PrimaryEndpoint()
	_, _ = garnet.Host()
	_, _ = garnet.Port()
	_, _ = garnet.UriExpression()
	_, _ = garnet.ConnectionStringExpression()

	app, err := builder.Build()
	if err != nil {
		log.Fatalf("Build: %v", err)
	}
	if err := app.Run(nil); err != nil {
		log.Fatalf("Run: %v", err)
	}
}
