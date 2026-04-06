import { createBuilder, ContainerLifetime } from './.modules/aspire.js';

const builder = await createBuilder();

const rabbitmq = await builder.addRabbitMQ("messaging");
await rabbitmq.withDataVolume();
await rabbitmq.withManagementPlugin();

const rabbitmq2 = await builder
    .addRabbitMQ("messaging2")
    .withLifetime(ContainerLifetime.Persistent)
    .withDataVolume()
    .withManagementPluginWithPort({ port: 15673 });

// ---- Property access on RabbitMQServerResource ----
const _endpoint = await rabbitmq.primaryEndpoint.get();
const _mgmtEndpoint = await rabbitmq.managementEndpoint.get();
const _host = await rabbitmq.host.get();
const _port = await rabbitmq.port.get();
const _uri = await rabbitmq.uriExpression.get();
const _userName = await rabbitmq.userNameReference.get();

const _cstr = await rabbitmq.connectionStringExpression.get();
await builder.build().run();