import { createBuilder } from './.aspire/modules/aspire.mjs';

const builder = await createBuilder();
const cache = builder.addRedis('cache');
const database = builder.addPostgres('postgres').addDatabase('app-db', { databaseName: 'native_app' });
const web = builder.addJavaScriptApp('web', './web')
    .withHttpEndpoint({ targetPort: 3000, env: 'PORT' })
    .withEnvironment('HOST', '127.0.0.1')
    .withEnvironment('NUXT_TELEMETRY_DISABLED', '1')
    .withEnvironment('NUXT_REDIS_URI', await cache.getConnectionProperty('uri'))
    .withReference(cache)
    .withReference(database)
    .waitFor(cache)
    .waitFor(database);
builder.addDevTunnel('tunnel').withTunnelReferenceAll(web, true);

await builder.build().run();
