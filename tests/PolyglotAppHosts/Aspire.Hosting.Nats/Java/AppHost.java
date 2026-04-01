import aspire.*;

void main() throws Exception {
        // Aspire TypeScript AppHost - NATS integration validation
        // Exercises all [AspireExport] methods for Aspire.Hosting.Nats
        var builder = DistributedApplication.CreateBuilder();
        // addNats - factory method with default options
        var nats = builder.addNats("messaging");
        // withJetStream - enable JetStream support
        nats.withJetStream();
        // withDataVolume - add persistent data volume
        nats.withDataVolume();
        // withDataVolume - with custom name and readOnly option
        var nats2 = builder.addNats("messaging2", new AddNatsOptions().port(4223.0))
            .withJetStream()
            .withDataVolume(new WithDataVolumeOptions().name("nats-data").isReadOnly(false))
            .withLifetime(ContainerLifetime.PERSISTENT);
        // withDataBindMount - bind mount a host directory
        var nats3 = builder.addNats("messaging3");
        nats3.withDataBindMount("/tmp/nats-data");
        // addNats - with custom userName and password parameters
        var customUser = builder.addParameter("nats-user");
        var customPass = builder.addParameter("nats-pass", true);
        var nats4 = builder.addNats("messaging4", new AddNatsOptions().userName(customUser).password(customPass));
        // withReference - a container referencing a NATS resource (connection string)
        var consumer = builder.addContainer("consumer", "myimage");
        consumer.withReference(nats);
        // withReference - with explicit connection name option
        consumer.withReference(nats4, new WithReferenceOptions().connectionName("messaging4-connection"));
        // ---- Property access on NatsServerResource ----
        var _endpoint = nats.primaryEndpoint();
        var _host = nats.host();
        var _port = nats.port();
        var _uri = nats.uriExpression();
        var _userName = nats.userNameReference();
        var _cstr = nats.connectionStringExpression();
        builder.build().run();
    }
