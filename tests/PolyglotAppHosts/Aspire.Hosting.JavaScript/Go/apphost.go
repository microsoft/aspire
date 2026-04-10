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

	nodeApp, err := builder.AddNodeApp("resource", "node", ".")
	if err != nil {
		log.Fatalf("AddNodeApp: %v", err)
	}
	_, _ = nodeApp.WithNpm(nil, nil, nil)
	_, _ = nodeApp.WithBun(nil, nil)
	_, _ = nodeApp.WithYarn(nil, nil)
	_, _ = nodeApp.WithPnpm(nil, nil)
	_, _ = nodeApp.WithBuildScript("build", nil)
	_, _ = nodeApp.WithRunScript("start", nil)

	javaScriptApp, err := builder.AddJavaScriptApp("resource", "node", nil)
	if err != nil {
		log.Fatalf("AddJavaScriptApp: %v", err)
	}
	_, _ = javaScriptApp.WithEnvironment("KEY", "value")

	viteApp, err := builder.AddViteApp("resource", "node", nil)
	if err != nil {
		log.Fatalf("AddViteApp: %v", err)
	}
	viteApp.WithViteConfig("vite.config.ts")
	_, _ = viteApp.WithPnpm(nil, nil)
	_, _ = viteApp.WithBuildScript("build", nil)
	_, _ = viteApp.WithRunScript("start", nil)

	app, err := builder.Build()
	if err != nil {
		log.Fatalf("Build: %v", err)
	}
	if err := app.Run(nil); err != nil {
		log.Fatalf("Run: %v", err)
	}
}
