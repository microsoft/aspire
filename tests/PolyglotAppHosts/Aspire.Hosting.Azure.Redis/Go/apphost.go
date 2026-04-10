// Aspire Go validation AppHost - Aspire.Hosting.Azure.Redis
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

	_, err = builder.AddAzureKeyVault("resource")
	if err != nil {
		log.Fatalf("AddAzureKeyVault: %v", err)
	}

	cache, err := builder.AddAzureManagedRedis("resource")
	if err != nil {
		log.Fatalf("AddAzureManagedRedis: %v", err)
	}

	accessKeyCache, err := builder.AddAzureManagedRedis("resource")
	if err != nil {
		log.Fatalf("AddAzureManagedRedis: %v", err)
	}

	containerCache, err := builder.AddAzureManagedRedis("resource")
	if err != nil {
		log.Fatalf("AddAzureManagedRedis: %v", err)
	}

	accessKeyCache.WithAccessKeyAuthentication()
	accessKeyCache.WithAccessKeyAuthenticationWithKeyVault(nil)
	containerCache.RunAsContainer(nil)

	_, _ = cache.ConnectionStringExpression()
	_, _ = cache.HostName()
	_, _ = cache.Port()
	_, _ = cache.UriExpression()
	_, _ = cache.UseAccessKeyAuthentication()

	app, err := builder.Build()
	if err != nil {
		log.Fatalf("Build: %v", err)
	}
	if err := app.Run(nil); err != nil {
		log.Fatalf("Run: %v", err)
	}
}
