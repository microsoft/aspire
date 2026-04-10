// Aspire Go validation AppHost - Aspire.Hosting.Valkey
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

	_, _ = builder.AddParameter("parameter", nil)

	valkey := builder.AddValkey("resource", nil, nil)
	if err = valkey.Err(); err != nil {
		log.Fatalf("valkey: %v", err)
	}

	_, _ = valkey.PrimaryEndpoint()
	_, _ = valkey.Host()
	_, _ = valkey.Port()
	_, _ = valkey.UriExpression()
	_, _ = valkey.ConnectionStringExpression()

	app, err := builder.Build()
	if err != nil {
		log.Fatalf("Build: %v", err)
	}
	if err := app.Run(nil); err != nil {
		log.Fatalf("Run: %v", err)
	}
}
