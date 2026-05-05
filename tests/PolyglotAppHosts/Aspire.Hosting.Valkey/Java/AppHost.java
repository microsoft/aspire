import aspire.*;

void main() throws Exception {
        var builder = DistributedApplication.CreateBuilder();
        var password = builder.addParameter("valkey-password", new AddParameterOptions().secret(true));
        var valkey = builder.addValkey("cache", new AddValkeyOptions().port(6380.0));
        valkey
            .withDataVolume(new WithDataVolumeOptions().name("valkey-data"))
            .withDataBindMount(".", true)
            .withPersistence(new WithPersistenceOptions().interval(100000000.0).keysChangedThreshold(1.0));
        // ---- Property access on ValkeyResource ----
        var _endpoint = valkey.primaryEndpoint();
        var _host = valkey.host();
        var _port = valkey.port();
        var _uri = valkey.uriExpression();
        var _cstr = valkey.connectionStringExpression();
        builder.build().run();
    }
