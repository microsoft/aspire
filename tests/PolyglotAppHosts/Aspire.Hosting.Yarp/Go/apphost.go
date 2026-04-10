// Aspire Go validation AppHost - Aspire.Hosting.Yarp
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

	isSecret := false
	buildVersionParam, _ := builder.AddParameterFromConfiguration("buildVersion", "MyConfig:BuildVersion", &isSecret)
	isSecret2 := true
	buildSecretParam, _ := builder.AddParameterFromConfiguration("buildSecret", "MyConfig:Secret", &isSecret2)

	builder.AddContainer("static-files-source", "nginx")
	builder.AddContainer("backend", "nginx")
	builder.AddProject("backend-service", "./src/BackendService", "http")
	builder.AddExternalService("external-backend", "https://example.com")

	proxy := builder.AddYarp("proxy")
	proxy.WithHostPort(8080)
	proxy.WithHostHttpsPort(8443)
	proxy.WithVolume("target", nil, nil)
	proxy.WithBuildArg("BUILD_VERSION", buildVersionParam)
	proxy.WithBuildSecret("MY_SECRET", buildSecretParam)
	proxy.PublishWithStaticFiles(nil)
	proxy.PublishAsConnectionString()
	if err = proxy.Err(); err != nil {
		log.Fatalf("proxy: %v", err)
	}

	app, err := builder.Build()
	if err != nil {
		log.Fatalf("Build: %v", err)
	}
	if err := app.Run(nil); err != nil {
		log.Fatalf("Run: %v", err)
	}
}
