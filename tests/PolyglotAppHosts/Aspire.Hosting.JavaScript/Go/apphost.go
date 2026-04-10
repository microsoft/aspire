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

	nodeApp := builder.AddNodeApp("resource", "node", ".")
	nodeApp.WithNpm(nil, nil, nil)
	nodeApp.WithBun(nil, nil)
	nodeApp.WithYarn(nil, nil)
	nodeApp.WithPnpm(nil, nil)
	nodeApp.WithBuildScript("build", nil)
	nodeApp.WithRunScript("start", nil)
	if err = nodeApp.Err(); err != nil {
		log.Fatalf("nodeApp: %v", err)
	}

	javaScriptApp := builder.AddJavaScriptApp("resource", "node", nil)
	javaScriptApp.WithEnvironment("KEY", "value")
	if err = javaScriptApp.Err(); err != nil {
		log.Fatalf("javaScriptApp: %v", err)
	}

	viteApp := builder.AddViteApp("resource", "node", nil)
	viteApp.WithViteConfig("vite.config.ts")
	viteApp.WithPnpm(nil, nil)
	viteApp.WithBuildScript("build", nil)
	viteApp.WithRunScript("start", nil)
	if err = viteApp.Err(); err != nil {
		log.Fatalf("viteApp: %v", err)
	}

	app, err := builder.Build()
	if err != nil {
		log.Fatalf("Build: %v", err)
	}
	if err := app.Run(nil); err != nil {
		log.Fatalf("Run: %v", err)
	}
}
