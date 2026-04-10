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
	if _, err = cache.WithDataVolume(nil, nil); err != nil {
		log.Fatalf("WithDataVolume: %v", err)
	}

	if _, err = cache.WithPersistence(nil, nil); err != nil {
		log.Fatalf("WithPersistence: %v", err)
	}

	// withDataBindMount on RedisResource
	if _, err = cache2.WithDataBindMount("/tmp/redis-data", nil); err != nil {
		log.Fatalf("WithDataBindMount: %v", err)
	}

	// withHostPort on RedisResource
	if _, err = cache.WithHostPort(6379); err != nil {
		log.Fatalf("WithHostPort: %v", err)
	}

	// withPassword on RedisResource
	secret2 := true
	newPassword, err := builder.AddParameter("new-redis-password", &secret2)
	if err != nil {
		log.Fatalf("AddParameter: %v", err)
	}
	if _, err = cache2.WithPassword(newPassword); err != nil {
		log.Fatalf("WithPassword: %v", err)
	}

	// withRedisCommander — with configureContainer callback exercising withHostPort
	_, err = cache.WithRedisCommander(func(args ...any) any {
		if len(args) > 0 {
			if commander, ok := args[0].(interface {
				WithHostPort(float64) (any, error)
			}); ok {
				commander.WithHostPort(8081)
			}
		}
		return nil
	}, nil)
	if err != nil {
		log.Fatalf("WithRedisCommander: %v", err)
	}

	// withRedisInsight — with configureContainer callback
	_, err = cache.WithRedisInsight(func(args ...any) any {
		if len(args) > 0 {
			if insight, ok := args[0].(interface {
				WithHostPort(float64) (any, error)
			}); ok {
				insight.WithHostPort(5540)
			}
		}
		return nil
	}, nil)
	if err != nil {
		log.Fatalf("WithRedisInsight: %v", err)
	}

	// ---- Property access on RedisResource (ExposeProperties = true) ----
	redis := cache
	_, _ = redis.PrimaryEndpoint()
	_, _ = redis.Host()
	_, _ = redis.Port()
	_, _ = redis.TlsEnabled()
	_, _ = redis.UriExpression()
	_, _ = redis.ConnectionStringExpression()

	app, err := builder.Build()
	if err != nil {
		log.Fatalf("Build: %v", err)
	}
	if err := app.Run(nil); err != nil {
		log.Fatalf("Run: %v", err)
	}
}
