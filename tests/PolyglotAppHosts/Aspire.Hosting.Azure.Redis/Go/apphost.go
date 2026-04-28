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

	keyVault := builder.AddAzureKeyVault("vault")
	if keyVault.Err() != nil {
		log.Fatalf(aspire.FormatError(keyVault.Err()))
	}
	var keyVaultResource aspire.AzureKeyVaultResource = keyVault

	cache := builder.AddAzureManagedRedis("cache")
	if cache.Err() != nil {
		log.Fatalf(aspire.FormatError(cache.Err()))
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
	if err := app.Run(nil); err != nil {
		log.Fatalf(aspire.FormatError(err))
	}
}
