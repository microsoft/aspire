package main

import (
	"log"

	"apphost/modules/aspire"
)

func main() {
	builder, err := aspire.CreateBuilder(nil)
	if err != nil {
		log.Fatalf(aspire.FormatError(err))
	}

	// addRedis — full overload with port and password parameter
	password := builder.AddParameter("redis-password", &aspire.AddParameterOptions{Secret: aspire.BoolPtr(true)})
	cache := builder.AddRedis("cache", &aspire.AddRedisOptions{Password: &password})
	if err = cache.Err(); err != nil {
		log.Fatalf(aspire.FormatError(err))
	}

	// addRedis — explicit port
	cache2 := builder.AddRedis("cache2", &aspire.AddRedisOptions{Port: aspire.Float64Ptr(6380)})
	if err = cache2.Err(); err != nil {
		log.Fatalf(aspire.FormatError(err))
	}

	cache.WithDataVolume(&aspire.WithDataVolumeOptions{Name: aspire.StringPtr("redis-data")}).
		WithPersistence(&aspire.WithPersistenceOptions{
			Interval:             aspire.Float64Ptr(60000000),
			KeysChangedThreshold: aspire.Float64Ptr(5),
		})
	if err = cache.Err(); err != nil {
		log.Fatalf(aspire.FormatError(err))
	}

	cache2.WithDataBindMount("/tmp/redis-data")
	if err = cache2.Err(); err != nil {
		log.Fatalf(aspire.FormatError(err))
	}

	// withHostPort on cache — stand-alone
	cache.WithHostPort(6379)
	if err = cache.Err(); err != nil {
		log.Fatalf(aspire.FormatError(err))
	}

	// withPassword on cache2
	newPassword := builder.AddParameter("new-redis-password", &aspire.AddParameterOptions{Secret: aspire.BoolPtr(true)})
	cache2.WithPassword(newPassword)
	if err = cache2.Err(); err != nil {
		log.Fatalf(aspire.FormatError(err))
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
			insight.WithDataVolume(&aspire.RedisInsightResourceWithDataVolumeOptions{Name: aspire.StringPtr("insight-data")})
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
		log.Fatalf(aspire.FormatError(err))
	}
	if err := app.Run(nil); err != nil {
		log.Fatalf(aspire.FormatError(err))
	}
}
