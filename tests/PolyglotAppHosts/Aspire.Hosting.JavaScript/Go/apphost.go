// Aspire Go validation AppHost - Aspire.Hosting.JavaScript
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

	nodeApp, err := builder.AddNodeApp("resource")
	if err != nil {
		log.Fatalf("AddNodeApp: %v", err)
	}
	_, _ = nodeApp.WithNpm()
	_, _ = nodeApp.WithBun()
	_, _ = nodeApp.WithYarn()
	_, _ = nodeApp.WithPnpm()
	_, _ = nodeApp.WithBuildScript()
	_, _ = nodeApp.WithRunScript()

	javaScriptApp, err := builder.AddJavaScriptApp("resource")
	if err != nil {
		log.Fatalf("AddJavaScriptApp: %v", err)
	}
	_, _ = javaScriptApp.WithEnvironment("KEY", "value")

	viteApp, err := builder.AddViteApp("resource")
	if err != nil {
		log.Fatalf("AddViteApp: %v", err)
	}
	_, _ = viteApp.WithViteConfig()
	_, _ = viteApp.WithPnpm()
	_, _ = viteApp.WithBuildScript()
	_, _ = viteApp.WithRunScript()

	app, err := builder.Build()
	if err != nil {
		log.Fatalf("Build: %v", err)
	}
	if err := app.Run(nil); err != nil {
		log.Fatalf("Run: %v", err)
	}
}
