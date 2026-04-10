import { createBuilder } from './.modules/aspire.js';

const builder = await createBuilder();
const customApiKey = await builder.addParameter('qdrant-key', { secret: true });
await builder.addQdrant('qdrant-custom', { apiKey: customApiKey, grpcPort: 16334, httpPort: 16333 });

const qdrant = await builder.addQdrant('qdrant');
await qdrant.withDataVolume({ name: 'qdrant-data' }).withDataBindMount('.', { isReadOnly: true });
const consumer = await builder.addContainer('consumer', 'busybox');
await consumer.withReference(qdrant, { connectionName: 'qdrant' });

// ---- Property access on QdrantServerResource ----
const _endpoint = await qdrant.primaryEndpoint.get();
const _grpcHost = await qdrant.grpcHost.get();
const _grpcPort = await qdrant.grpcPort.get();
const _httpEndpoint = await qdrant.httpEndpoint.get();
const _httpHost = await qdrant.httpHost.get();
const _httpPort = await qdrant.httpPort.get();
const _uri = await qdrant.uriExpression.get();
const _httpUri = await qdrant.httpUriExpression.get();

const _cstr = await qdrant.connectionStringExpression.get();
await builder.build().run();
