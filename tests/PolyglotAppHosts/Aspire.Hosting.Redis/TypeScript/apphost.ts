import { createBuilder } from './.modules/aspire.js';

const builder = await createBuilder();

// addRedis — full overload with port and password parameter
const password = await builder.addParameter("redis-password", { secret: true });
const cache = await builder.addRedis("cache", { password: password });

// addRedisWithPort — overload with explicit port
const cache2 = await builder.addRedisWithPort("cache2", { port: 6380 });

// withDataVolume + withPersistence — fluent chaining on RedisResource
await cache.withDataVolume({ name: "redis-data" });
await cache.withPersistence({ interval: 600000000, keysChangedThreshold: 5 });

// withDataBindMount on RedisResource
await cache2.withDataBindMount("/tmp/redis-data");

// withHostPort on RedisResource
await cache.withHostPort({ port: 6379 });

// withPassword on RedisResource
const newPassword = await builder.addParameter("new-redis-password", { secret: true });
await cache2.withPassword(newPassword);

// withRedisCommander — with configureContainer callback exercising withHostPort
await cache.withRedisCommander({
    configureContainer: async (commander) => {
        await commander.withHostPort({ port: 8081 });
    },
    containerName: "my-commander"
});

// withRedisInsight — with configureContainer callback exercising withHostPort, withDataVolume, withDataBindMount
await cache.withRedisInsight({
    configureContainer: async (insight) => {
        await insight.withHostPort({ port: 5540 });
        await insight.withDataVolume({ name: "insight-data" });
        await insight.withDataBindMount("/tmp/insight-data");
    },
    containerName: "my-insight"
});

// ---- Property access on RedisResource (ExposeProperties = true) ----
const redis = await cache;
const _endpoint = await redis.primaryEndpoint.get();
const _host = await redis.host.get();
const _port = await redis.port.get();
const _tlsEnabled: boolean = await redis.tlsEnabled.get();
const _uri = await redis.uriExpression.get();
const _cstr = await redis.connectionStringExpression.get();

await builder.build().run();
