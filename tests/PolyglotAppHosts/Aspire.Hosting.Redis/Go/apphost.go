// Aspire Go validation AppHost - Aspire.Hosting.Redis
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

	// addRedis — full overload with port and password parameter
	secret := true
	password := builder.AddParameter("redis-password", &secret)

	cache := builder.AddRedis("cache", nil, password).WithDataVolume(nil, nil).WithPersistence(nil, nil)
	if err = cache.Err(); err != nil {
		log.Fatalf("cache: %v", err)
	}

	// addRedisWithPort — overload with explicit port
	var port float64 = 6380
	// withDataBindMount + withHostPort on cache2 — fluent chain
	secret2 := true
	newPassword := builder.AddParameter("new-redis-password", &secret2)
	cache2 := builder.AddRedisWithPort("cache2", port).WithDataBindMount("/tmp/redis-data", nil).WithHostPort(6380).WithPassword(newPassword)
	if err = cache2.Err(); err != nil {
		log.Fatalf("cache2: %v", err)
	}

	// withHostPort on cache — stand-alone (after the first chain)
	cache.WithHostPort(6379)
	if err = cache.Err(); err != nil {
		log.Fatalf("cache: %v", err)
	}

	// withRedisCommander — fluent (1 return value)
	cache.WithRedisCommander(func(args ...any) any {
		if len(args) > 0 {
			if commander, ok := args[0].(interface {
				WithHostPort(float64) *aspire.ContainerResource
			}); ok {
				commander.WithHostPort(8081)
			}
		}
		return nil
	}, nil)

	// withRedisInsight — fluent (1 return value)
	cache.WithRedisInsight(func(args ...any) any {
		if len(args) > 0 {
			if insight, ok := args[0].(interface {
				WithHostPort(float64) *aspire.ContainerResource
			}); ok {
				insight.WithHostPort(5540)
			}
		}
		return nil
	}, nil)

	// ---- Property access on RedisResource (ExposeProperties = true) ----
	_, _ = cache.PrimaryEndpoint()
	_, _ = cache.Host()
	_, _ = cache.Port()
	_, _ = cache.TlsEnabled()
	_, _ = cache.UriExpression()
	_, _ = cache.ConnectionStringExpression()

	app, err := builder.Build()
	if err != nil {
		log.Fatalf("Build: %v", err)
	}
	if err := app.Run(nil); err != nil {
		log.Fatalf("Run: %v", err)
	}
}
