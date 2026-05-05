import { createBuilder } from './.modules/aspire.js';

const builder = await createBuilder();

const adminPassword = await builder.addParameter("seq-admin-password", { secret: true });
const seq = await builder.addSeq("seq", adminPassword, { port: 5341 });

await seq.withDataVolume();
await seq.withDataVolume({ name: "seq-data", isReadOnly: false });
await seq.withDataBindMount("./seq-data", { isReadOnly: true });

// ---- Property access on SeqResource ----
const _endpoint = await seq.primaryEndpoint();
const _host = await seq.host();
const _port = await seq.port();
const _uri = await seq.uriExpression();

const _cstr = await seq.connectionStringExpression();
await builder.build().run();
