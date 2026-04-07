// Aspire TypeScript AppHost — Kafka integration validation
// Exercises all [AspireExport] methods for Aspire.Hosting.Kafka

import { createBuilder } from './.modules/aspire.js';

const builder = await createBuilder();

// addKafka — factory method with optional port
const kafka = await builder.addKafka("broker");

// withKafkaUI — adds Kafka UI management container with callback
const kafkaWithUi = await kafka.withKafkaUI({
    configureContainer: async (ui) => {
        // withHostPort — sets the host port for Kafka UI
        await ui.withHostPort({ port: 9000 });
    },
    containerName: "my-kafka-ui",
});

// withDataVolume — adds a data volume
await kafkaWithUi.withDataVolume();

// withDataBindMount — adds a data bind mount
const kafka2 = await builder.addKafka("broker2", { port: 19092 });
await kafka2.withDataBindMount("/tmp/kafka-data");

// ---- Property access on KafkaServerResource ----
const _endpoint = await kafka.primaryEndpoint.get();
const _host = await kafka.host.get();
const _port = await kafka.port.get();
const _internal = await kafka.internalEndpoint.get();

const _cstr = await kafka.connectionStringExpression.get();
await builder.build().run();
