import aspire.*;

void main() throws Exception {
        // Aspire TypeScript AppHost - Milvus integration validation
        // Exercises every exported member of Aspire.Hosting.Milvus
        var builder = DistributedApplication.CreateBuilder();
        // ── 1. addMilvus: basic Milvus server resource ─────────────────────────────
        var milvus = builder.addMilvus("milvus");
        // ── 2. addMilvus: with custom apiKey parameter ─────────────────────────────
        var customKey = builder.addParameter("milvus-key", true);
        var milvus2 = builder.addMilvus("milvus2", new AddMilvusOptions().apiKey(customKey));
        // ── 3. addMilvus: with explicit gRPC port ──────────────────────────────────
        builder.addMilvus("milvus3", new AddMilvusOptions().grpcPort(19531.0));
        // ── 4. addDatabase: add database to Milvus server ──────────────────────────
        var db = milvus.addDatabase("mydb");
        // ── 5. addDatabase: with custom database name ──────────────────────────────
        milvus.addDatabase("db2", "customdb");
        // ── 6. withAttu: add Attu administration tool ──────────────────────────────
        milvus.withAttu();
        // ── 7. withAttu: with container name ────────────────────────────────────────
        milvus2.withAttu(new WithAttuOptions().containerName("my-attu"));
        // ── 8. withAttu: with configureContainer callback ──────────────────────────
        builder.addMilvus("milvus-attu-cfg")
            .withAttu(new WithAttuOptions().configureContainer((container) -> {
                    container.withHttpEndpoint(new WithHttpEndpointOptions().port(3001.0));
                }));
        // ── 9. withDataVolume: persistent data volume ──────────────────────────────
        milvus.withDataVolume();
        // ── 10. withDataVolume: with custom name ────────────────────────────────────
        milvus2.withDataVolume(new WithDataVolumeOptions().name("milvus-data"));
        // ── 11. withDataBindMount: bind mount for data ─────────────────────────────
        builder.addMilvus("milvus-bind")
            .withDataBindMount("./milvus-data");
        // ── 12. withDataBindMount: with read-only flag ─────────────────────────────
        builder.addMilvus("milvus-bind-ro")
            .withDataBindMount("./milvus-data-ro", true);
        // ── 13. withConfigurationFile: custom milvus.yaml ──────────────────────────
        builder.addMilvus("milvus-cfg")
            .withConfigurationFile("./milvus.yaml");
        // ── 14. Fluent chaining: multiple With* methods ────────────────────────────
        var milvusChained = builder.addMilvus("milvus-chained");
        milvusChained.withLifetime(ContainerLifetime.PERSISTENT);
        milvusChained.withDataVolume(new WithDataVolumeOptions().name("milvus-chained-data"));
        milvusChained.withAttu();
        // ── 15. withReference: use Milvus database from a container resource ───────
        var api = builder.addContainer("api", "myregistry/myapp");
        api.withReference(db);
        // ── 16. withReference: use Milvus server directly ──────────────────────────
        api.withReference(milvus);
        // ---- Property access on MilvusServerResource ----
        var _endpoint = milvus.primaryEndpoint();
        var _host = milvus.host();
        var _port = milvus.port();
        var _token = milvus.token();
        var _uri = milvus.uriExpression();
        var _cstr = milvus.connectionStringExpression();
        var _databases = milvus.databases();
        builder.build().run();
    }
