import aspire.*;

void main() throws Exception {
        var builder = DistributedApplication.CreateBuilder();
        var customApiKey = builder.addParameter("qdrant-key", new AddParameterOptions().secret(true));
        builder.addQdrant("qdrant-custom", new AddQdrantOptions().apiKey(customApiKey).grpcPort(16334.0).httpPort(16333.0));
        var qdrant = builder.addQdrant("qdrant");
        qdrant.withDataVolume(new WithDataVolumeOptions().name("qdrant-data")).withDataBindMount(".", true);
        var consumer = builder.addContainer("consumer", "busybox");
        consumer.withReference(qdrant, new WithReferenceOptions().connectionName("qdrant"));
        // ---- Property access on QdrantServerResource ----
        var _endpoint = qdrant.primaryEndpoint();
        var _grpcHost = qdrant.grpcHost();
        var _grpcPort = qdrant.grpcPort();
        var _httpEndpoint = qdrant.httpEndpoint();
        var _httpHost = qdrant.httpHost();
        var _httpPort = qdrant.httpPort();
        var _uri = qdrant.uriExpression();
        var _httpUri = qdrant.httpUriExpression();
        var _cstr = qdrant.connectionStringExpression();
        builder.build().run();
    }
