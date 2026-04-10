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

	kubernetes, err := builder.AddKubernetesEnvironment("resource")
	if err != nil {
		log.Fatalf("AddKubernetesEnvironment: %v", err)
	}
	_, _ = kubernetes.WithProperties()
	_, _ = kubernetes.HelmChartName()
	_, _ = kubernetes.DefaultStorageClassName()
	_, _ = kubernetes.DefaultServiceType()

	serviceContainer, err := builder.AddContainer("resource", "image")
	if err != nil {
		log.Fatalf("AddContainer: %v", err)
	}
	_, _ = serviceContainer.PublishAsKubernetesService()

	app, err := builder.Build()
	if err != nil {
		log.Fatalf("Build: %v", err)
	}
	if err := app.Run(nil); err != nil {
		log.Fatalf("Run: %v", err)
	}
}
