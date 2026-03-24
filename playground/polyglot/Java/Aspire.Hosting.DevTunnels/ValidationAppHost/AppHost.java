import aspire.*;

void main() throws Exception {
        var builder = DistributedApplication.CreateBuilder();
        // Test 1: Basic dev tunnel resource creation (addDevTunnel)
        var tunnel = builder.addDevTunnel("mytunnel");
        // Test 2: addDevTunnel with tunnelId option
        var tunnel2 = builder.addDevTunnel("mytunnel2", new AddDevTunnelOptions().tunnelId("custom-tunnel-id"));
        // Test 3: withAnonymousAccess
        builder.addDevTunnel("anon-tunnel")
            .withAnonymousAccess();
        // Test 4: Add a container to reference its endpoints
        var web = builder.addContainer("web", "nginx");
        web.withHttpEndpoint(new WithHttpEndpointOptions().port(80.0));
        // Test 5: withTunnelReference with EndpointReference (expose a specific endpoint)
        var webEndpoint = web.getEndpoint("http");
        tunnel.withTunnelReference(webEndpoint);
        // Test 6: withTunnelReferenceAnonymous with EndpointReference + allowAnonymous
        var web2 = builder.addContainer("web2", "nginx");
        web2.withHttpEndpoint(new WithHttpEndpointOptions().port(8080.0));
        var web2Endpoint = web2.getEndpoint("http");
        tunnel2.withTunnelReferenceAnonymous(web2Endpoint, true);
        // Test 7: withTunnelReferenceAll - expose all endpoints on a resource
        var tunnel3 = builder.addDevTunnel("all-endpoints-tunnel");
        var web3 = builder.addContainer("web3", "nginx");
        web3.withHttpEndpoint(new WithHttpEndpointOptions().port(80.0));
        tunnel3.withTunnelReferenceAll(web3, false);
        // Test 8: getTunnelEndpoint - get the public tunnel endpoint for a specific endpoint
        var web4 = builder.addContainer("web4", "nginx");
        web4.withHttpEndpoint(new WithHttpEndpointOptions().port(80.0));
        var web4Endpoint = web4.getEndpoint("http");
        var tunnel4 = builder.addDevTunnel("get-endpoint-tunnel");
        tunnel4.withTunnelReference(web4Endpoint);
        var _tunnelEndpoint = tunnel4.getTunnelEndpoint(web4Endpoint);
        // Test 9: addDevTunnel with the dedicated polyglot parameters
        var tunnel5 = builder.addDevTunnel("configured-tunnel", new AddDevTunnelOptions().tunnelId("configured-tunnel-id").allowAnonymous(true).description("Configured by the polyglot validation app").labels(new String[] { "validation", "polyglot" }));
        var web5 = builder.addContainer("web5", "nginx");
        web5.withHttpEndpoint(new WithHttpEndpointOptions().port(9090.0));
        var web5Endpoint = web5.getEndpoint("http");
        tunnel5.withTunnelReferenceAnonymous(web5Endpoint, true);
        // Test 10: Chained configuration
        builder.addDevTunnel("chained-tunnel")
            .withAnonymousAccess();
        builder.build().run();
    }
