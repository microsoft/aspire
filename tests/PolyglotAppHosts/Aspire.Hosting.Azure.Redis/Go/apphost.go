package main

import (
	"log"

	"apphost/modules/aspire"
)

func main() {
	builder, err := aspire.CreateBuilder()
	if err != nil {
		log.Fatalf(aspire.FormatError(err))
	}

	keyVault := builder.AddAzureKeyVault("vault")
	if keyVault.Err() != nil {
		log.Fatalf(aspire.FormatError(keyVault.Err()))
	}
	var keyVaultResource aspire.AzureKeyVaultResource = keyVault

	cache := builder.AddAzureManagedRedis("cache")
	cache.ConfigureInfrastructure(func(infrastructure aspire.AzureResourceInfrastructure) {
		cluster := infrastructure.GetRedisEnterpriseCluster()
		if err := cluster.SetMinimumTlsVersion(aspire.RedisEnterpriseTlsVersionTls1_2).Err(); err != nil {
			log.Fatalf(aspire.FormatError(err))
		}
		if _, err := cluster.MinimumTlsVersion(); err != nil {
			log.Fatalf(aspire.FormatError(err))
		}
	})
	if cache.Err() != nil {
		log.Fatalf(aspire.FormatError(cache.Err()))
	}

	// The obsolete Azure Redis hosting API is not exported.
	legacyCache := builder.AddAzureInfrastructure("legacyRedis", func(infrastructure aspire.AzureResourceInfrastructure) {
		redis := infrastructure.AddRedisResource("legacyRedis")
		sku := infrastructure.CreateRedisSku()
		if err := sku.SetName(aspire.RedisSkuNameBasic).Err(); err != nil {
			log.Fatalf(aspire.FormatError(err))
		}
		if err := sku.SetFamily(aspire.RedisSkuFamilyBasicOrStandard).Err(); err != nil {
			log.Fatalf(aspire.FormatError(err))
		}
		if err := sku.SetCapacity(float64(0)).Err(); err != nil {
			log.Fatalf(aspire.FormatError(err))
		}
		if err := redis.SetSku(sku).Err(); err != nil {
			log.Fatalf(aspire.FormatError(err))
		}
	})
	legacyCache.ConfigureInfrastructure(func(infrastructure aspire.AzureResourceInfrastructure) {
		redis := infrastructure.GetRedisResource()
		if err := redis.SetEnableNonSslPort(false).Err(); err != nil {
			log.Fatalf(aspire.FormatError(err))
		}
		if _, err := redis.EnableNonSslPort(); err != nil {
			log.Fatalf(aspire.FormatError(err))
		}
	})
	if legacyCache.Err() != nil {
		log.Fatalf(aspire.FormatError(legacyCache.Err()))
	}

	accessKeyCache := builder.AddAzureManagedRedis("cache-access-key")
	if accessKeyCache.Err() != nil {
		log.Fatalf(aspire.FormatError(accessKeyCache.Err()))
	}

	containerCache := builder.AddAzureManagedRedis("cache-container")
	if containerCache.Err() != nil {
		log.Fatalf(aspire.FormatError(containerCache.Err()))
	}

	accessKeyCache.WithAccessKeyAuthentication()
	accessKeyCache.WithAccessKeyAuthentication(&aspire.WithAccessKeyAuthenticationOptions{KeyVaultBuilder: &keyVaultResource})
	if accessKeyCache.Err() != nil {
		log.Fatalf(aspire.FormatError(accessKeyCache.Err()))
	}

	containerCache.RunAsContainer(&aspire.RunAsContainerOptions{
		ConfigureContainer: func(container aspire.RedisResource) {
			container.WithVolume("/data")
		},
	})
	if containerCache.Err() != nil {
		log.Fatalf(aspire.FormatError(containerCache.Err()))
	}

	_ = cache.ConnectionStringExpression()
	_ = cache.HostName()
	_ = cache.Id()
	_ = cache.NameOutputReference()
	_ = cache.Port()
	_ = cache.UriExpression()
	_, _ = cache.UseAccessKeyAuthentication()

	_ = accessKeyCache.ConnectionStringExpression()
	_ = accessKeyCache.HostName()
	_ = accessKeyCache.Password()
	_ = accessKeyCache.UriExpression()
	_, _ = accessKeyCache.UseAccessKeyAuthentication()

	_ = containerCache.ConnectionStringExpression()
	_ = containerCache.HostName()
	_ = containerCache.Password()
	_ = containerCache.Port()
	_ = containerCache.UriExpression()

	app, err := builder.Build()
	if err != nil {
		log.Fatalf(aspire.FormatError(err))
	}
	if err := app.Run(); err != nil {
		log.Fatalf(aspire.FormatError(err))
	}
}
