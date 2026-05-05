import { createBuilder } from './.modules/aspire.js';

const builder = await createBuilder();

const provider = await builder.addConnectionString("provider");

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
await silo.withReference(orleansClient);

const client = await builder.addContainer("client", "redis");
await client.withReference(orleansClient);

await builder.build().run();
