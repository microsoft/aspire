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

	_, _ = builder.AddPythonApp("resource", ".", "script.py")
	_, _ = builder.AddPythonModule("resource", ".", "module")
	_, _ = builder.AddPythonExecutable("resource", ".", "script.py")

	uvicorn, err := builder.AddUvicornApp("resource", ".", "app:app")
	if err != nil {
		log.Fatalf("AddUvicornApp: %v", err)
	}
	_, _ = uvicorn.WithVirtualEnvironment(".venv", nil)
	_, _ = uvicorn.WithDebugging()
	_, _ = uvicorn.WithEntrypoint(aspire.EntrypointType("uvicorn"), "app:app")
	_, _ = uvicorn.WithPip(nil, nil)
	_, _ = uvicorn.WithUv(nil, nil)

	app, err := builder.Build()
	if err != nil {
		log.Fatalf("Build: %v", err)
	}
	if err := app.Run(nil); err != nil {
		log.Fatalf("Run: %v", err)
	}
}
