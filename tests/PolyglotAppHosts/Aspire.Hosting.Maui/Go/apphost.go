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

	maui := builder.AddMauiProject("resource", ".")
	maui.AddWindowsDevice("resource")
	maui.AddMacCatalystDevice("resource")
	maui.AddAndroidDevice("resource", nil)
	maui.AddAndroidEmulator("resource", nil)
	maui.AddiOSDevice("device-id", nil)
	maui.AddiOSSimulator("simulator-id", nil)
	if err = maui.Err(); err != nil {
		log.Fatalf("maui: %v", err)
	}

	app, err := builder.Build()
	if err != nil {
		log.Fatalf("Build: %v", err)
	}
	if err := app.Run(nil); err != nil {
		log.Fatalf("Run: %v", err)
	}
}
