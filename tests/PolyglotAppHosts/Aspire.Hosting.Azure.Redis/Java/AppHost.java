import aspire.*;

void main() throws Exception {
        var builder = DistributedApplication.CreateBuilder();
        var keyVault = builder.addAzureKeyVault("vault");
        var cache = builder.addAzureManagedRedis("cache");
        cache.configureInfrastructure(infrastructure -> {
            var cluster = infrastructure.getRedisEnterpriseCluster();
            cluster.setMinimumTlsVersion(RedisEnterpriseTlsVersion.TLS1_2);
            var _minimumTlsVersion = cluster.minimumTlsVersion();
        });
        // The obsolete Azure Redis hosting API is not exported.
        var legacyCache = builder.addAzureInfrastructure("legacyRedis", infrastructure -> {
            var redis = infrastructure.addRedisResource("legacyRedis");
            var sku = infrastructure.createRedisSku();
            sku.setName(RedisSkuName.BASIC);
            sku.setFamily(RedisSkuFamily.BASIC_OR_STANDARD);
            sku.setCapacity(0);
            redis.setSku(sku);
        });
        legacyCache.configureInfrastructure(infrastructure -> {
            var redis = infrastructure.getRedisResource();
            redis.setEnableNonSslPort(false);
            var _enableNonSslPort = redis.enableNonSslPort();
        });
        var accessKeyCache = builder.addAzureManagedRedis("cache-access-key");
        var containerCache = builder.addAzureManagedRedis("cache-container");
        accessKeyCache.withAccessKeyAuthentication();
        accessKeyCache.withAccessKeyAuthentication(keyVault);
        containerCache.runAsContainer((container) -> {
                container.withVolume("/data");
            });
        var _connectionString = cache.connectionStringExpression();
        var _hostName = cache.hostName();
        var _port = cache.port();
        var _uri = cache.uriExpression();
        var _useAccessKeyAuthentication = cache.useAccessKeyAuthentication();
        var _accessKeyConnectionString = accessKeyCache.connectionStringExpression();
        var _accessKeyHostName = accessKeyCache.hostName();
        var _accessKeyPassword = accessKeyCache.password();
        var _accessKeyUri = accessKeyCache.uriExpression();
        var _usesAccessKeyAuthentication = accessKeyCache.useAccessKeyAuthentication();
        var _containerConnectionString = containerCache.connectionStringExpression();
        var _containerHostName = containerCache.hostName();
        var _containerPort = containerCache.port();
        var _containerPassword = containerCache.password();
        var _containerUri = containerCache.uriExpression();
        builder.build().run();
    }
