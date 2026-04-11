import { createBuilder } from './.modules/aspire.js';

const builder = await createBuilder();

const provider = await builder.addConnectionString("provider", { environmentVariableName: "ORLEANS_PROVIDER_CONNECTION_STRING" });

const orleans = await builder.addOrleans("orleans")
    .withClusterId("cluster-id")
    .withServiceId("service-id")
    .withClustering(provider)
    .withDevelopmentClustering()
    .withGrainStorage("grain-storage", provider)
    .withMemoryGrainStorage("memory-grain-storage")
    .withStreaming("streaming", provider)
    .withMemoryStreaming("memory-streaming")
    .withBroadcastChannel("broadcast")
    .withReminders(provider)
    .withMemoryReminders()
    .withGrainDirectory("grain-directory", provider);

const orleansClient = await orleans.asClient();

const silo = await builder.addContainer("silo", "redis");
await silo.withOrleansReference(orleans);

const client = await builder.addContainer("client", "redis");
await client.withOrleansClientReference(orleansClient);

await builder.build().run();
