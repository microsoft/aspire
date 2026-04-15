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
	password := builder.AddParameterWithOpts("redis-password", &aspire.AddParameterOptions{Secret: func() *bool { b := true; return &b }()})
	cache := builder.AddRedisWithOpts("cache", &aspire.AddRedisOptions{Password: password})
	cache.WithDataVolumeWithOpts(&aspire.WithDataVolumeOptions{Name: func() *string { s := "redis-data"; return &s }()})
	cache.WithPersistenceWithOpts(&aspire.WithPersistenceOptions{
		Interval:             func() *float64 { v := float64(600000000); return &v }(),
		KeysChangedThreshold: func() *float64 { v := float64(5); return &v }(),
	})
	if err = cache.Err(); err != nil {
		log.Fatalf("cache: %v", err)
	}

	// addRedisWithPort — overload with explicit port
	cache2 := builder.AddRedisWithPort("cache2", 6380)
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
	newPassword := builder.AddParameterWithOpts("new-redis-password", &aspire.AddParameterOptions{Secret: func() *bool { b := true; return &b }()})
	cache2.WithPassword(newPassword)
	if err = cache2.Err(); err != nil {
		log.Fatalf("cache2 password: %v", err)
	}

	// withRedisCommander — with configureContainer callback
	cache.WithRedisCommanderWithOpts(&aspire.WithRedisCommanderOptions{ContainerName: func() *string { s := "my-commander"; return &s }()}, func(commander *aspire.RedisCommanderResource) {
		commander.WithHostPort(8081)
	})

	// withRedisInsight — with configureContainer callback
	cache.WithRedisInsightWithOpts(&aspire.WithRedisInsightOptions{ContainerName: func() *string { s := "my-insight"; return &s }()}, func(insight *aspire.RedisInsightResource) {
		insight.WithHostPort(5540)
		insight.WithDataVolumeWithOpts(&aspire.RedisInsightResourceWithDataVolumeOptions{Name: func() *string { s := "insight-data"; return &s }()})
		insight.WithDataBindMount("/tmp/insight-data")
	})
	if err = cache.Err(); err != nil {
		log.Fatalf("cache after commander/insight: %v", err)
	}

	// ---- Property access on RedisResource ----
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
