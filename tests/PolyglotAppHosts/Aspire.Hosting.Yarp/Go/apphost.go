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

	buildVersionParam, _ := builder.AddParameterFromConfiguration("buildVersion", "MyConfig:BuildVersion", false)
	buildSecretParam, _ := builder.AddParameterFromConfiguration("buildSecret", "MyConfig:Secret", true)

	staticFilesSource, err := builder.AddContainer("static-files-source", "nginx")
	if err != nil {
		log.Fatalf("AddContainer: %v", err)
	}

	_, err = builder.AddContainer("backend", "nginx")
	if err != nil {
		log.Fatalf("AddContainer: %v", err)
	}

	_, err = builder.AddProject("backend-service", "./src/BackendService", "http")
	if err != nil {
		log.Fatalf("AddProject: %v", err)
	}

	_, err = builder.AddExternalService("external-backend", "https://example.com")
	if err != nil {
		log.Fatalf("AddExternalService: %v", err)
	}

	proxy, err := builder.AddYarp("proxy")
	if err != nil {
		log.Fatalf("AddYarp: %v", err)
	}
	_, _ = proxy.WithHostPort(8080)
	_, _ = proxy.WithHostHttpsPort(8443)
	_, _ = proxy.WithVolume()
	_, _ = proxy.WithBuildArg("BUILD_VERSION", buildVersionParam)
	_, _ = proxy.WithBuildSecret("MY_SECRET", buildSecretParam)
	_, _ = proxy.PublishWithStaticFiles(staticFilesSource)
	_, _ = proxy.WithConfig(func(args ...any) any { return nil })
	_, _ = proxy.PublishAsConnectionString()

	app, err := builder.Build()
	if err != nil {
		log.Fatalf("Build: %v", err)
	}
	if err := app.Run(nil); err != nil {
		log.Fatalf("Run: %v", err)
	}
}
