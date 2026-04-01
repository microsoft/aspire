// Aspire TypeScript AppHost — Milvus integration validation
// Exercises every exported member of Aspire.Hosting.Milvus

import { createBuilder, ContainerLifetime } from './.modules/aspire.js';

const builder = await createBuilder();

// ── 1. addMilvus: basic Milvus server resource ─────────────────────────────
const milvus = await builder.addMilvus("milvus");

// ── 2. addMilvus: with custom apiKey parameter ─────────────────────────────
const customKey = await builder.addParameter("milvus-key", { secret: true });
const milvus2 = await builder.addMilvus("milvus2", { apiKey: customKey });

// ── 3. addMilvus: with explicit gRPC port ──────────────────────────────────
await builder.addMilvus("milvus3", { grpcPort: 19531 });

// ── 4. addDatabase: add database to Milvus server ──────────────────────────
const db = await milvus.addDatabase("mydb");

// ── 5. addDatabase: with custom database name ──────────────────────────────
await milvus.addDatabase("db2", { databaseName: "customdb" });

// ── 6. withAttu: add Attu administration tool ──────────────────────────────
await milvus.withAttu();

// ── 7. withAttu: with container name ────────────────────────────────────────
await milvus2.withAttu({ containerName: "my-attu" });

// ── 8. withAttu: with configureContainer callback ──────────────────────────
await builder.addMilvus("milvus-attu-cfg")
    .withAttu({
        configureContainer: async (container) => {
            await container.withHttpEndpoint({ port: 3001 });
        }
    });

// ── 9. withDataVolume: persistent data volume ──────────────────────────────
await milvus.withDataVolume();

// ── 10. withDataVolume: with custom name ────────────────────────────────────
await milvus2.withDataVolume({ name: "milvus-data" });

// ── 11. withDataBindMount: bind mount for data ─────────────────────────────
await builder.addMilvus("milvus-bind")
    .withDataBindMount("./milvus-data");

// ── 12. withDataBindMount: with read-only flag ─────────────────────────────
await builder.addMilvus("milvus-bind-ro")
    .withDataBindMount("./milvus-data-ro", { isReadOnly: true });

// ── 13. withConfigurationFile: custom milvus.yaml ──────────────────────────
await builder.addMilvus("milvus-cfg")
    .withConfigurationFile("./milvus.yaml");

// ── 14. Fluent chaining: multiple With* methods ────────────────────────────
await builder.addMilvus("milvus-chained")
    .withLifetime(ContainerLifetime.Persistent)
    .withDataVolume({ name: "milvus-chained-data" })
    .withAttu();

// ── 15. withReference: use Milvus database from a container resource ───────
const api = await builder.addContainer("api", "myregistry/myapp");
await api.withReference(db);

// ── 16. withReference: use Milvus server directly ──────────────────────────
await api.withReference(milvus);

// ---- Property access on MilvusServerResource ----
const _endpoint = await milvus.primaryEndpoint.get();
const _host = await milvus.host.get();
const _port = await milvus.port.get();
const _token = await milvus.token.get();
const _uri = await milvus.uriExpression.get();

const _cstr = await milvus.connectionStringExpression.get();
const _databases = milvus.databases;
await builder.build().run();
