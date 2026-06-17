import aspire.*;

void main() throws Exception {
        // Aspire TypeScript AppHost - Kafka integration validation
        // Exercises all [AspireExport] methods for Aspire.Hosting.Kafka
        var builder = DistributedApplication.CreateBuilder();
        // addKafka - factory method with optional port
        var kafka = builder.addKafka("broker");
        // withKafkaUI - adds Kafka UI management container with callback
        var kafkaWithUi = kafka.withKafkaUI(new WithKafkaUIOptions().configureContainer((ui) -> {
                // withHostPort - sets the host port for Kafka UI
                ui.withHostPort(9000.0);
            }).containerName("my-kafka-ui"));
        // withDataVolume - adds a data volume
        kafkaWithUi.withDataVolume();
        // withDataBindMount - adds a data bind mount
        var kafka2 = builder.addKafka("broker2", 19092.0);
        kafka2.withDataBindMount("/tmp/kafka-data");
        // ---- Property access on KafkaServerResource ----
        var _endpoint = kafka.primaryEndpoint();
        var _host = kafka.host();
        var _port = kafka.port();
        var _internal = kafka.internalEndpoint();
        var _cstr = kafka.connectionStringExpression();
        builder.build().run();
    }
