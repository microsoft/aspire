import aspire.*;

void main() throws Exception {
        var builder = DistributedApplication.CreateBuilder();
        var keyVault = builder.addAzureKeyVault("vault");
        var cache = builder.addAzureManagedRedis("cache");
        var accessKeyCache = builder.addAzureManagedRedis("cache-access-key");
        var containerCache = builder.addAzureManagedRedis("cache-container");
        accessKeyCache.withAccessKeyAuthentication();
        accessKeyCache.withAccessKeyAuthenticationWithKeyVault(keyVault);
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
