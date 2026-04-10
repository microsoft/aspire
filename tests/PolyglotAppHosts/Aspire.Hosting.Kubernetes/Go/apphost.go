// Aspire Go validation AppHost - Aspire.Hosting.Kubernetes
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

	kubernetes := builder.AddKubernetesEnvironment("resource")
	kubernetes.WithProperties(nil)
	_, _ = kubernetes.HelmChartName()
	_, _ = kubernetes.DefaultStorageClassName()
	_, _ = kubernetes.DefaultServiceType()
	if err = kubernetes.Err(); err != nil {
		log.Fatalf("kubernetes: %v", err)
	}

	serviceContainer := builder.AddContainer("resource", "image")
	_, _ = serviceContainer.PublishAsKubernetesService(nil)
	if err = serviceContainer.Err(); err != nil {
		log.Fatalf("serviceContainer: %v", err)
	}

	app, err := builder.Build()
	if err != nil {
		log.Fatalf("Build: %v", err)
	}
	if err := app.Run(nil); err != nil {
		log.Fatalf("Run: %v", err)
	}
}
