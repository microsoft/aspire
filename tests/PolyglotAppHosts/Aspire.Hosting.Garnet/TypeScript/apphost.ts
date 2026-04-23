import { createBuilder } from './.modules/aspire.js';

const builder = await createBuilder();

const cache = await builder.addGarnet("cache");

// ---- Property access on GarnetResource ----
const garnet = await cache;
const _endpoint = await garnet.primaryEndpoint();
const _host = await garnet.host();
const _port = await garnet.port();
const _uri = await garnet.uriExpression();

const _cstr = await garnet.connectionStringExpression();
await builder.build().run();
