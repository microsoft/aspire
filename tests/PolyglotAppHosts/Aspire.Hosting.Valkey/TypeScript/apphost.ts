import { createBuilder } from './.modules/aspire.js';

const builder = await createBuilder();
const password = await builder.addParameter('valkey-password', { secret: true });
const valkey = await builder.addValkey('cache', { port: 6380, password });

await valkey
    .withDataVolume({ name: 'valkey-data' })
    .withDataBindMount('.', { isReadOnly: true })
    .withPersistence({ interval: 100000000, keysChangedThreshold: 1 });

// ---- Property access on ValkeyResource ----
const _endpoint = await valkey.primaryEndpoint.get();
const _host = await valkey.host.get();
const _port = await valkey.port.get();
const _uri = await valkey.uriExpression.get();

const _cstr = await valkey.connectionStringExpression.get();
await builder.build().run();
