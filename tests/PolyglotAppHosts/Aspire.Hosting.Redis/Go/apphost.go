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
	password, err := builder.AddParameter("redis-password", &secret)
	if err != nil {
		log.Fatalf("AddParameter: %v", err)
	}

	cache, err := builder.AddRedis("cache", nil, password)
	if err != nil {
		log.Fatalf("AddRedis: %v", err)
	}

	// addRedisWithPort — overload with explicit port
	var port float64 = 6380
	cache2, err := builder.AddRedisWithPort("cache2", port)
	if err != nil {
		log.Fatalf("AddRedisWithPort: %v", err)
	}

	// withDataVolume + withPersistence — fluent chaining on RedisResource
	if err := cache.WithDataVolume(nil, nil).WithPersistence(nil, nil).Err(); err != nil {
		log.Fatalf("cache setup: %v", err)
	}

	// withDataBindMount + withHostPort on cache2 — fluent chain
	secret2 := true
	newPassword, err := builder.AddParameter("new-redis-password", &secret2)
	if err != nil {
		log.Fatalf("AddParameter: %v", err)
	}
	if err := cache2.WithDataBindMount("/tmp/redis-data", nil).WithHostPort(6380).WithPassword(newPassword).Err(); err != nil {
		log.Fatalf("cache2 setup: %v", err)
	}

	// withHostPort on cache — stand-alone (after the first chain)
	if err := cache.WithHostPort(6379).Err(); err != nil {
		log.Fatalf("WithHostPort: %v", err)
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
