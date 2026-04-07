import { createBuilder } from './.modules/aspire.js';

const builder = await createBuilder();

const keyVault = await builder.addAzureKeyVault("vault");
const cache = await builder.addAzureManagedRedis("cache");
const accessKeyCache = await builder.addAzureManagedRedis("cache-access-key");
const containerCache = await builder.addAzureManagedRedis("cache-container");

await accessKeyCache.withAccessKeyAuthentication();
await accessKeyCache.withAccessKeyAuthenticationWithKeyVault(keyVault);

await containerCache.runAsContainer({
    configureContainer: async (container) => {
        await container.withVolume("/data");
    }
});

const _connectionString = await cache.connectionStringExpression.get();
const _hostName = await cache.hostName.get();
const _port = await cache.port.get();
const _uri = await cache.uriExpression.get();
const _useAccessKeyAuthentication: boolean = await cache.useAccessKeyAuthentication.get();

const _accessKeyConnectionString = await accessKeyCache.connectionStringExpression.get();
const _accessKeyHostName = await accessKeyCache.hostName.get();
const _accessKeyPassword = await accessKeyCache.password.get();
const _accessKeyUri = await accessKeyCache.uriExpression.get();
const _usesAccessKeyAuthentication: boolean = await accessKeyCache.useAccessKeyAuthentication.get();

const _containerConnectionString = await containerCache.connectionStringExpression.get();
const _containerHostName = await containerCache.hostName.get();
const _containerPort = await containerCache.port.get();
const _containerPassword = await containerCache.password.get();
const _containerUri = await containerCache.uriExpression.get();

await builder.build().run();
