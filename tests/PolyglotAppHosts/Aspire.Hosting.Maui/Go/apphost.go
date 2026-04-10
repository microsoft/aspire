// Aspire Go validation AppHost - Aspire.Hosting.Maui
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

	maui, err := builder.AddMauiProject("resource", ".")
	if err != nil {
		log.Fatalf("AddMauiProject: %v", err)
	}
	_, _ = maui.AddWindowsDevice("resource")
	_, _ = maui.AddMacCatalystDevice("resource")
	_, _ = maui.AddAndroidDevice("resource", nil)
	_, _ = maui.AddAndroidEmulator("resource", nil)
	_, _ = maui.AddiOSDevice("device-id", nil)
	_, _ = maui.AddiOSSimulator("simulator-id", nil)

	app, err := builder.Build()
	if err != nil {
		log.Fatalf("Build: %v", err)
	}
	if err := app.Run(nil); err != nil {
		log.Fatalf("Run: %v", err)
	}
}
