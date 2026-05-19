import { createBuilder } from './.modules/aspire.js';

const builder = await createBuilder();
const password = await builder.addParameter('valkey-password', { secret: true });
const valkey = await builder.addValkey('cache', { port: 6380, password });

await valkey
    .withDataVolume({ name: 'valkey-data' })
    .withDataBindMount('.', { isReadOnly: true })
    .withPersistence({ interval: 100000000, keysChangedThreshold: 1 });

// ---- Property access on ValkeyResource ----
const _endpoint = await valkey.primaryEndpoint();
const _host = await valkey.host();
const _port = await valkey.port();
const _uri = await valkey.uriExpression();

const _cstr = await valkey.connectionStringExpression();
await builder.build().run();
