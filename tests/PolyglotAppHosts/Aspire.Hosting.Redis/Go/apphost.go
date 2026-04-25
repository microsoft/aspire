package main

import (
	"log"

	"apphost/modules/aspire"
)

func main() {
	builder, err := aspire.CreateBuilder()
	if err != nil {
		log.Fatalf("CreateBuilder: %v", err)
	}

	// addRedis — full overload with port and password parameter
	password := builder.AddParameter("redis-password", &aspire.AddParameterOptions{Secret: aspire.BoolPtr(true)})
	cache := builder.AddRedis("cache", &aspire.AddRedisOptions{Password: &password})
	if err = cache.Err(); err != nil {
		log.Fatalf("cache: %v", err)
	}

	// addRedisWithPort — overload with explicit port
	cache2 := builder.AddRedisWithPort("cache2", 6380)
	if err = cache2.Err(); err != nil {
		log.Fatalf("cache2: %v", err)
	}

	cache.WithDataVolume(&aspire.WithDataVolumeOptions{Name: aspire.StringPtr("redis-data")}).
		WithPersistence(&aspire.WithPersistenceOptions{
			Interval:             aspire.Float64Ptr(60000000),
			KeysChangedThreshold: aspire.Float64Ptr(5),
		})
	if err = cache.Err(); err != nil {
		log.Fatalf("cache: %v", err)
	}

	cache2.WithDataBindMount("/tmp/redis-data")
	if err = cache2.Err(); err != nil {
		log.Fatalf("cache2: %v", err)
	}

	// withHostPort on cache — stand-alone
	cache.WithHostPort(6379)
	if err = cache.Err(); err != nil {
		log.Fatalf("cache host port: %v", err)
	}

	// withPassword on cache2
	newPassword := builder.AddParameter("new-redis-password", &aspire.AddParameterOptions{Secret: aspire.BoolPtr(true)})
	cache2.WithPassword(newPassword)
	if err = cache2.Err(); err != nil {
		log.Fatalf("cache2 password: %v", err)
	}

	// withRedisCommander — with configureContainer callback exercising WithHostPort
	cache.WithRedisCommander(&aspire.WithRedisCommanderOptions{
		ConfigureContainer: func(commander aspire.RedisCommanderResource) {
			commander.WithHostPort(8081)
		},
		ContainerName: aspire.StringPtr("my-commander"),
	})

	// withRedisInsight — with configureContainer callback exercising WithHostPort, WithDataVolume, WithDataBindMount
	cache.WithRedisInsight(&aspire.WithRedisInsightOptions{
		ConfigureContainer: func(insight aspire.RedisInsightResource) {
			insight.WithHostPort(5540)
			insight.WithDataVolume(&aspire.WithDataVolumeOptions{Name: aspire.StringPtr("insight-data")})
			insight.WithDataBindMount("/tmp/insight-data")
		},
		ContainerName: aspire.StringPtr("my-insight"),
	})

	// ---- Property access on RedisResource (ExposeProperties = true) ----
	_, _ = cache.PrimaryEndpoint().EndpointName()
	_, _ = cache.PrimaryEndpoint().Host()
	_, _ = cache.PrimaryEndpoint().Port()
	_, _ = cache.TlsEnabled()
	_ = cache.UriExpression()
	_ = cache.ConnectionStringExpression()

	app, err := builder.Build()
	if err != nil {
		log.Fatalf("Build: %v", err)
	}
	if err := app.Run(); err != nil {
		log.Fatalf("Run: %v", err)
	}
}
