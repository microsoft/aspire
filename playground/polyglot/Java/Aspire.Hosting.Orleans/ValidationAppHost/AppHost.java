import aspire.*;

void main() throws Exception {
        var builder = DistributedApplication.CreateBuilder();
        var provider = builder.addConnectionString("provider", "ORLEANS_PROVIDER_CONNECTION_STRING");
        var orleans = builder.addOrleans("orleans")
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
        var orleansClient = orleans.asClient();
        var silo = builder.addContainer("silo", "redis");
        silo.withOrleansReference(orleans);
        var client = builder.addContainer("client", "redis");
        client.withOrleansClientReference(orleansClient);
        builder.build().run();
    }
