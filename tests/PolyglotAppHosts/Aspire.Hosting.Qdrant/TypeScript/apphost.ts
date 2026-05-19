import { createBuilder } from './.modules/aspire.js';

const builder = await createBuilder();
const customApiKey = await builder.addParameter('qdrant-key', { secret: true });
await builder.addQdrant('qdrant-custom', { apiKey: customApiKey, grpcPort: 16334, httpPort: 16333 });

const qdrant = await builder.addQdrant('qdrant');
await qdrant.withDataVolume({ name: 'qdrant-data' }).withDataBindMount('.', { isReadOnly: true });
const consumer = await builder.addContainer('consumer', 'busybox');
await consumer.withReference(qdrant, { connectionName: 'qdrant' });

// ---- Property access on QdrantServerResource ----
const _endpoint = await qdrant.primaryEndpoint();
const _grpcHost = await qdrant.grpcHost();
const _grpcPort = await qdrant.grpcPort();
const _httpEndpoint = await qdrant.httpEndpoint();
const _httpHost = await qdrant.httpHost();
const _httpPort = await qdrant.httpPort();
const _uri = await qdrant.uriExpression();
const _httpUri = await qdrant.httpUriExpression();

const _cstr = await qdrant.connectionStringExpression();
await builder.build().run();
