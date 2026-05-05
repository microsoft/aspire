import { createBuilder, ContainerLifetime } from './.modules/aspire.js';

const builder = await createBuilder();

const rabbitmq = await builder.addRabbitMQ("messaging");
await rabbitmq.withDataVolume();
await rabbitmq.withManagementPlugin();

const rabbitmq2 = await builder
    .addRabbitMQ("messaging2")
    .withLifetime(ContainerLifetime.Persistent)
    .withDataVolume()
    .withManagementPlugin({ port: 15673 });

// ---- Property access on RabbitMQServerResource ----
const _endpoint = await rabbitmq.primaryEndpoint();
const _mgmtEndpoint = await rabbitmq.managementEndpoint();
const _host = await rabbitmq.host();
const _port = await rabbitmq.port();
const _uri = await rabbitmq.uriExpression();
const _userName = await rabbitmq.userNameReference();

const _cstr = await rabbitmq.connectionStringExpression();
await builder.build().run();
