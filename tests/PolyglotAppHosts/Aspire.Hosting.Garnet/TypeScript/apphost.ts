import { createBuilder } from './.modules/aspire.js';

const builder = await createBuilder();

const cache = await builder.addGarnet("cache");

// ---- Property access on GarnetResource ----
const garnet = await cache;
const _endpoint = await garnet.primaryEndpoint.get();
const _host = await garnet.host.get();
const _port = await garnet.port.get();
const _uri = await garnet.uriExpression.get();

const _cstr = await garnet.connectionStringExpression.get();
await builder.build().run();
