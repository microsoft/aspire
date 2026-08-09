import aspire.*;

void main() throws Exception {
        var builder = DistributedApplication.CreateBuilder();
        var cache = builder.addGarnet("cache");
        // ---- Property access on GarnetResource ----
        var garnet = cache;
        var _endpoint = garnet.primaryEndpoint();
        var _host = garnet.host();
        var _port = garnet.port();
        var _uri = garnet.uriExpression();
        var _cstr = garnet.connectionStringExpression();
        builder.build().run();
    }
