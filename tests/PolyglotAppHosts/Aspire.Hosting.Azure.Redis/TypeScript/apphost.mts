import { RedisEnterpriseTlsVersion, RedisSkuFamily, RedisSkuName, createBuilder } from './.aspire/modules/aspire.mjs';

const builder = await createBuilder();

const keyVault = await builder.addAzureKeyVault("vault");
const cache = await builder.addAzureManagedRedis("cache");
await cache.configureInfrastructure(async infrastructure => {
    const cluster = await infrastructure.getRedisEnterpriseCluster();
    await cluster.minimumTlsVersion.set(RedisEnterpriseTlsVersion.Tls1_2);
    const _minimumTlsVersion = await cluster.minimumTlsVersion.get();
});

// The legacy Azure Redis hosting API is obsolete and is not exported to polyglot hosts.
const legacyCache = await builder.addAzureInfrastructure("legacyRedis", async infrastructure => {
    const redis = await infrastructure.addRedisResource("legacyRedis");
    const sku = await infrastructure.createRedisSku();
    await sku.name.set(RedisSkuName.Basic);
    await sku.family.set(RedisSkuFamily.BasicOrStandard);
    await sku.capacity.set(0);
    await redis.sku.set(sku);
});
await legacyCache.configureInfrastructure(async infrastructure => {
    const redis = await infrastructure.getRedisResource();
    await redis.enableNonSslPort.set(false);
    const _enableNonSslPort = await redis.enableNonSslPort.get();
});
const accessKeyCache = await builder.addAzureManagedRedis("cache-access-key");
const containerCache = await builder.addAzureManagedRedis("cache-container");

await accessKeyCache.withAccessKeyAuthentication();
await accessKeyCache.withAccessKeyAuthentication({ keyVaultBuilder: keyVault });

await containerCache.runAsContainer({
    configureContainer: async (container) => {
        await container.withVolume("/data");
    }
});

const _connectionString = await cache.connectionStringExpression();
const _hostName = await cache.hostName();
const _nameOutputReference = await cache.nameOutputReference();
const _resourceId = await cache.id();
const _port = await cache.port();
const _uri = await cache.uriExpression();
const _useAccessKeyAuthentication: boolean = await cache.useAccessKeyAuthentication();

const _accessKeyConnectionString = await accessKeyCache.connectionStringExpression();
const _accessKeyHostName = await accessKeyCache.hostName();
const _accessKeyPassword = await accessKeyCache.password();
const _accessKeyUri = await accessKeyCache.uriExpression();
const _usesAccessKeyAuthentication: boolean = await accessKeyCache.useAccessKeyAuthentication();

const _containerConnectionString = await containerCache.connectionStringExpression();
const _containerHostName = await containerCache.hostName();
const _containerPort = await containerCache.port();
const _containerPassword = await containerCache.password();
const _containerUri = await containerCache.uriExpression();

await builder.build().run();
