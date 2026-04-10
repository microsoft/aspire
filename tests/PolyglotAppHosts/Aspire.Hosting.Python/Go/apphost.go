// Aspire Go validation AppHost - Aspire.Hosting.Python
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

	_, _ = builder.AddPythonApp("resource")
	_, _ = builder.AddPythonModule("resource")
	_, _ = builder.AddPythonExecutable("resource")

	uvicorn, err := builder.AddUvicornApp("resource")
	if err != nil {
		log.Fatalf("AddUvicornApp: %v", err)
	}
	_, _ = uvicorn.WithVirtualEnvironment()
	_, _ = uvicorn.WithDebugging()
	_, _ = uvicorn.WithEntrypoint()
	_, _ = uvicorn.WithPip()
	_, _ = uvicorn.WithUv()

	app, err := builder.Build()
	if err != nil {
		log.Fatalf("Build: %v", err)
	}
	if err := app.Run(nil); err != nil {
		log.Fatalf("Run: %v", err)
	}
}
