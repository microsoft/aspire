import { createBuilder } from './.modules/aspire.js';

const builder = await createBuilder();

const adminPassword = await builder.addParameter("seq-admin-password", { secret: true });
const seq = await builder.addSeq("seq", adminPassword, { port: 5341 });

await seq.withDataVolume();
await seq.withDataVolume({ name: "seq-data", isReadOnly: false });
await seq.withDataBindMount("./seq-data", { isReadOnly: true });

// ---- Property access on SeqResource ----
const _endpoint = await seq.primaryEndpoint.get();
const _host = await seq.host.get();
const _port = await seq.port.get();
const _uri = await seq.uriExpression.get();

const _cstr = await seq.connectionStringExpression.get();
await builder.build().run();
