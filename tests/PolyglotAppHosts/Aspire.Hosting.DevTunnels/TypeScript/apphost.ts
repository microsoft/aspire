import { createBuilder } from './.modules/aspire.js';

const builder = await createBuilder();

// Test 1: Basic dev tunnel resource creation (addDevTunnel)
const tunnel = await builder.addDevTunnel("mytunnel");

// Test 2: addDevTunnel with tunnelId option
const tunnel2 = await builder.addDevTunnel("mytunnel2", { tunnelId: "custom-tunnel-id" });

// Test 3: withAnonymousAccess
await builder.addDevTunnel("anon-tunnel")
    .withAnonymousAccess();

// Test 4: Add a container to reference its endpoints
const web = await builder.addContainer("web", "nginx")
    .withHttpEndpoint({ port: 80 });

// Test 5: withTunnelReference with EndpointReference (expose a specific endpoint)
const webEndpoint = await web.getEndpoint("http");
await tunnel.withTunnelReference(webEndpoint);

// Test 6: withTunnelReferenceAnonymous with EndpointReference + allowAnonymous
const web2 = await builder.addContainer("web2", "nginx")
    .withHttpEndpoint({ port: 8080 });
const web2Endpoint = await web2.getEndpoint("http");
await tunnel2.withTunnelReferenceAnonymous(web2Endpoint, true);

// Test 7: withTunnelReferenceAll - expose all endpoints on a resource
const tunnel3 = await builder.addDevTunnel("all-endpoints-tunnel");
const web3 = await builder.addContainer("web3", "nginx")
    .withHttpEndpoint({ port: 80 });
await tunnel3.withTunnelReferenceAll(web3, false);

// Test 8: getTunnelEndpoint - get the public tunnel endpoint for a specific endpoint
const web4 = await builder.addContainer("web4", "nginx")
    .withHttpEndpoint({ port: 80 });
const web4Endpoint = await web4.getEndpoint("http");
const tunnel4 = await builder.addDevTunnel("get-endpoint-tunnel");
await tunnel4.withTunnelReference(web4Endpoint);
const _tunnelEndpoint = await tunnel4.getTunnelEndpoint(web4Endpoint);

// Test 9: addDevTunnel with the dedicated polyglot parameters
const tunnel5 = await builder.addDevTunnel("configured-tunnel", {
    tunnelId: "configured-tunnel-id",
    allowAnonymous: true,
    description: "Configured by the polyglot validation app",
    labels: ["validation", "polyglot"]
});
const web5 = await builder.addContainer("web5", "nginx")
    .withHttpEndpoint({ port: 9090 });
const web5Endpoint = await web5.getEndpoint("http");
await tunnel5.withTunnelReferenceAnonymous(web5Endpoint, true);

// Test 10: Chained configuration
await builder.addDevTunnel("chained-tunnel")
    .withAnonymousAccess();

await builder.build().run();
