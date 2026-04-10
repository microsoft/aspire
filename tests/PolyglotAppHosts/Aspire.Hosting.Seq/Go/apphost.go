// Aspire Go validation AppHost - Aspire.Hosting.Seq
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

	_, _ = builder.AddParameter("parameter")

	seq, err := builder.AddSeq("resource")
	if err != nil {
		log.Fatalf("AddSeq: %v", err)
	}
	_, _ = seq.WithDataVolume()
	_, _ = seq.WithDataVolume()
	_, _ = seq.WithDataBindMount()

	_, _ = seq.PrimaryEndpoint()
	_, _ = seq.Host()
	_, _ = seq.Port()
	_, _ = seq.UriExpression()
	_, _ = seq.ConnectionStringExpression()

	app, err := builder.Build()
	if err != nil {
		log.Fatalf("Build: %v", err)
	}
	if err := app.Run(nil); err != nil {
		log.Fatalf("Run: %v", err)
	}
}
