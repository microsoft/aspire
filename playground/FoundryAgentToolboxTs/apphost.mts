// FoundryAgentToolboxTs sample
//
// Demonstrates wiring a Foundry Agent that calls a custom MCP server via the Foundry Toolbox,
// from a TypeScript AppHost, where the MCP server requires bearer-token authentication.
//
// Topology (run mode):
//
//   mcp-bearer-token (parameter, secret)
//        |
//        +--> mcp-server (Node, Express, @modelcontextprotocol/sdk)
//        |        validates Authorization: Bearer <token> on /mcp
//        |
//   mcp-tunnel (dev tunnel, anonymous public ingress)
//        |   exposes mcp-server's http endpoint at https://<id>.devtunnels.ms
//        |
//        +--> foundry.project.field-tools toolbox
//                 .withMcpTool('directory', refExpr`${publicEndpoint}/mcp`,
//                              { authorizationToken: refExpr`${bearerToken}`, headers: { ... } })
//
// Topology (publish mode / aspire deploy):
//
//   mcp-server is deployed to Azure Container Apps with external HTTPS ingress, so the Foundry
//   data plane can reach it directly at the ACA FQDN. The dev tunnel is omitted from the model
//   (it's a run-mode helper only).
//
// Caveat (Foundry data-plane limitation, December 2025): the inline `authorization` /  `headers`
// fields that `ResponseTool.CreateMcpTool(...)` writes into the toolbox version JSON are NOT
// currently honored by the Foundry toolbox proxy when it makes outbound calls to the MCP server.
// The documented auth surface for toolbox-deployed MCP tools is `project_connection_id`
// referencing a Foundry project connection that holds the credential
// (see https://learn.microsoft.com/en-us/azure/foundry/agents/how-to/tools/toolbox?pivots=javascript).
// The .NET `ResponseTool.CreateMcpTool` overload doesn't expose `project_connection_id` yet, so
// this sample's bearer-auth round trip currently 401s when invoked through a deployed toolbox.
// The AppHost wiring is still correct (the toolbox version is created with the expected fields and
// the MCP server enforces auth correctly when called directly), but a full Foundry-agent->MCP
// round trip via the toolbox is blocked until the Foundry SDK / data plane closes that gap.

import { createBuilder, refExpr, FoundryModels } from './.aspire/modules/aspire.mjs';

const builder = await createBuilder();

const ec = await builder.executionContext();
const isPublishMode = await ec.isPublishMode();

const bearerToken = await builder.addParameter('mcp-bearer-token', { secret: true });

// In publish mode we need an explicit Azure Container Apps environment so the NodeApp below
// has somewhere to be deployed. (The Foundry project's own ContainerRegistry isn't a full ACA
// env, only a registry, so without this the deploy pipeline silently skips the NodeApp.)
let cae;
if (isPublishMode) {
    cae = await builder.addAzureContainerAppEnvironment('cae');
}

// Build the MCP server. In publish mode we mark the http endpoint as external so Azure Container
// Apps provisions a public HTTPS ingress, which is how the Foundry data plane in Azure reaches
// the server. In run mode the endpoint stays local and is exposed via a dev tunnel below.
let mcpServer = builder
    .addNodeApp('mcp-server', './mcp-server', 'src/server.ts')
    .withRunScript('start')
    .withHttpEndpoint({ env: 'PORT' })
    .withEnvironment('MCP_BEARER_TOKEN', bearerToken);

if (isPublishMode) {
    // Pin the NodeApp to the ACA env so the deploy pipeline knows where to host it (the Foundry
    // project also implements IAzureComputeEnvironmentResource, so without an explicit binding
    // the pipeline refuses to pick between them).
    if (cae) {
        mcpServer = mcpServer.withComputeEnvironment(cae);
    }
    mcpServer = mcpServer.withExternalHttpEndpoints();
}

const mcpServerResource = await mcpServer;
const mcpEndpoint = mcpServerResource.getEndpoint('http');

// Resolve the public-from-Foundry URL for the MCP server. In publish mode this is the ACA ingress
// FQDN; in run mode it's a dev tunnel front-end.
let publicMcpEndpoint;
if (isPublishMode) {
    // Awaited because refExpr below needs a resolved handle, not a Promise.
    publicMcpEndpoint = await mcpEndpoint;
} else {
    // Anonymous tunnel so Foundry can reach it without tunnel-level auth; the MCP server's bearer
    // middleware is still the gate that actually protects tool invocations.
    const mcpTunnel = await builder.addDevTunnel('mcp-tunnel', { allowAnonymous: true });
    await mcpTunnel.withTunnelReferenceAnonymous(mcpEndpoint, true);
    publicMcpEndpoint = await mcpTunnel.getTunnelEndpoint(mcpEndpoint);
}

const foundry = await builder.addFoundry('foundry');
const project = await foundry.addProject('project');

// Chat model the Foundry-side agent will use. The toolbox tools are visible to any agent in the
// project that opts into the toolbox.
const _chat = await project.addModelDeployment('chat', FoundryModels.OpenAI.Gpt41Mini);

const toolbox = await project.addToolbox('field-tools', { version: 'v1' });

// A built-in Foundry tool, mainly to show the toolbox can mix Foundry-managed and custom tools.
await toolbox.withWebSearchTool();

// The custom MCP tool. The MCP server mounts at /mcp (per the spec's conventional path), so the
// base URL needs that suffix appended via refExpr. The Foundry data plane calls the resulting
// public URL over HTTPS and sends the bearer token in the Authorization header.
await toolbox.withMcpTool('directory', refExpr`${publicMcpEndpoint}/mcp`, {
    authorizationToken: refExpr`${bearerToken}`,
    headers: {
        // Custom tracing/tenant header so operators can correlate Foundry-initiated calls in the
        // MCP server logs.
        'x-app-source': refExpr`foundry-toolbox-ts-sample`
    }
});

await builder.build().run();
