// Aspire TypeScript AppHost - Validation for Aspire.Hosting.Azure.Sandboxes

import {
    AzureConnectorNamespaceMcpAccessPolicyPrincipalType,
    AzureSandboxAutoDeleteTrigger,
    AzureSandboxAutoSuspendMode,
    AzureSandboxTier,
    createBuilder
} from './.aspire/modules/aspire.mjs';

const builder = await createBuilder();

const connectorNamespace = await builder.addAzureConnectorNamespace("connectors");
const outlook = await connectorNamespace.addConnection("outlook", "office365", {
    connectionName: "office365-outlook",
    displayName: "Office 365 Outlook"
});
await outlook.withAccessPolicy("worker-access", {
    policyName: "worker-acl",
    objectId: "11111111-1111-1111-1111-111111111111",
    tenantId: "22222222-2222-2222-2222-222222222222"
});
const sandboxIdentity = await builder.addAzureUserAssignedIdentity("sandbox-identity");
await outlook.withIdentityAccessPolicy(
    "sandbox-identity-access",
    sandboxIdentity,
    { policyName: "sandbox-identity-acl" });
const outlookMcp = await connectorNamespace.addMcpServerConfig("outlook-mcp", {
    description: "Allow-listed Outlook tools."
});
await outlookMcp.withConnector("office365", outlook, {
    operations: [
        {
            name: "GetEmailsV3"
        }
    ]
});
await outlookMcp.withAccessPolicy("developer-access", {
    objectId: "33333333-3333-3333-3333-333333333333",
    tenantId: "22222222-2222-2222-2222-222222222222",
    principalType: AzureConnectorNamespaceMcpAccessPolicyPrincipalType.User
});

const sandboxes = await builder.addAzureSandboxGroup("sandboxes");
await sandboxes.withUserAssignedIdentity(sandboxIdentity);
const pullIdentity = await builder.addAzureUserAssignedIdentity("sandbox-pull-identity");
await sandboxes.withAcrPullIdentity(pullIdentity);

const api = await builder
    .addContainer("api", "mcr.microsoft.com/dotnet/runtime-deps:10.0")
    .withHttpEndpoint({ name: "http", targetPort: 8080 })
    .withExternalHttpEndpoints();
await api.withReference(outlook);

const publishedApi = await api.publishAsAzureSandbox(sandboxes, {
    tier: AzureSandboxTier.Large,
    autoSuspendEnabled: true,
    autoSuspendInterval: 900_000,
    autoSuspendMode: AzureSandboxAutoSuspendMode.Disk,
    autoDeleteEnabled: true,
    autoDeleteInterval: 3_600_000,
    autoDeleteTrigger: AzureSandboxAutoDeleteTrigger.AfterSuspend,
    publicEndpointReadyTimeout: 120_000,
    endpoints: [
        {
            name: "http",
            anonymous: false
        }
    ]
});
await publishedApi.withRemoteImageTag("validated-compute-handle");

await builder.build().run();
