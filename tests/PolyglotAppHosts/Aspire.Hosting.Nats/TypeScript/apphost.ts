// Aspire TypeScript AppHost — NATS integration validation
// Exercises all [AspireExport] methods for Aspire.Hosting.Nats

import { createBuilder, ContainerLifetime } from './.modules/aspire.js';

const builder = await createBuilder();

// addNats — factory method with default options
const nats = await builder.addNats("messaging");

// withJetStream — enable JetStream support
await nats.withJetStream();

// withDataVolume — add persistent data volume
await nats.withDataVolume();

// withDataVolume — with custom name and readOnly option
const nats2 = await builder.addNats("messaging2", { port: 4223 })
    .withJetStream()
    .withDataVolume({ name: "nats-data", isReadOnly: false })
    .withLifetime(ContainerLifetime.Persistent);

// withDataBindMount — bind mount a host directory
const nats3 = await builder.addNats("messaging3");
await nats3.withDataBindMount("/tmp/nats-data");

// addNats — with custom userName and password parameters
const customUser = await builder.addParameter("nats-user");
const customPass = await builder.addParameter("nats-pass", { secret: true });
const nats4 = await builder.addNats("messaging4", {
    userName: customUser,
    password: customPass,
});

// withReference — a container referencing a NATS resource (connection string)
const consumer = await builder.addContainer("consumer", "myimage");
await consumer.withReference(nats);

// withReference — with explicit connection name option
await consumer.withReference(nats4, { connectionName: "messaging4-connection" });

// ---- Property access on NatsServerResource ----
const _endpoint = await nats.primaryEndpoint();
const _host = await nats.host();
const _port = await nats.port();
const _uri = await nats.uriExpression();
const _userName = await nats.userNameReference();

const _cstr = await nats.connectionStringExpression();
await builder.build().run();
