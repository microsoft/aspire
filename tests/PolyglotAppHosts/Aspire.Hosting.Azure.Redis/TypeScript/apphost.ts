import { createBuilder } from './.modules/aspire.js';

const builder = await createBuilder();

const keyVault = await builder.addAzureKeyVault("vault");
const cache = await builder.addAzureManagedRedis("cache");
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
