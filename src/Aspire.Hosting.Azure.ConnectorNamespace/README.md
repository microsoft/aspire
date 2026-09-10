# Azure Connector Namespace hosting integration

Use this integration to model, configure, and orchestrate Azure Connector Namespace resources in an Aspire solution.

## Getting started

### Prerequisites

* An Azure subscription and region with Azure Connector Namespace preview access.
* Permission to create Connector Namespace resources, connections, MCP server configurations, and access policies.
* A user account authorized to complete any connector-specific OAuth or consent flow.

### Add the integration

From your AppHost directory, add the `Aspire.Hosting.Azure.ConnectorNamespace` integration with the Aspire CLI:

```bash
aspire add Aspire.Hosting.Azure.ConnectorNamespace
```

## Usage example

A Connector Namespace contains separately managed, reusable resources:

* A **connection** is an authenticated binding to an external service such as Office 365 or SharePoint.
* An **MCP server configuration** exposes selected connector operations as MCP tools.
* `WithConnector` links the MCP server configuration to the connection it uses. The current service preview supports one connector per managed MCP server configuration.

The following AppHost example adds a Connector Namespace with a connection and an allow-listed managed MCP server:

**C#**

```csharp
var connectorNamespace = builder.AddAzureConnectorNamespace("connectors");

var outlook = connectorNamespace.AddConnection(
    "outlook",
    "office365",
    new AzureConnectorNamespaceConnectionOptions
    {
        ConnectionName = "office365-outlook",
        DisplayName = "Office 365 Outlook"
    })
    .WithAccessPolicy(
        "worker-access",
        new AzureConnectorNamespaceAccessPolicyOptions
        {
            ObjectId = "33333333-3333-3333-3333-333333333333",
            TenantId = "22222222-2222-2222-2222-222222222222"
        });

builder.AddProject<Projects.Worker>("worker")
    .WithReference(outlook);

connectorNamespace.AddMcpServerConfig("outlook-mcp")
    .WithConnector(
        "office365",
        outlook,
        new AzureConnectorNamespaceMcpConnectorOptions
        {
            Operations =
            [
                new AzureConnectorNamespaceMcpOperationOptions
                {
                    Name = "GetEmailsV3",
                    DisplayName = "Get emails"
                }
            ]
        })
    .WithAccessPolicy(
        "developer-access",
        new AzureConnectorNamespaceMcpAccessPolicyOptions
        {
            ObjectId = "11111111-1111-1111-1111-111111111111",
            TenantId = "22222222-2222-2222-2222-222222222222",
            PrincipalType = AzureConnectorNamespaceMcpAccessPolicyPrincipalType.User
        });
```

**TypeScript**

```typescript
import { AzureConnectorNamespaceMcpAccessPolicyPrincipalType } from "./.aspire/modules/aspire.mjs";

const connectorNamespace = await builder.addAzureConnectorNamespace("connectors");

const outlook = await connectorNamespace.addConnection("outlook", "office365", {
    connectionName: "office365-outlook",
    displayName: "Office 365 Outlook"
});
await outlook.withAccessPolicy("worker-access", {
    objectId: "33333333-3333-3333-3333-333333333333",
    tenantId: "22222222-2222-2222-2222-222222222222"
});

const worker = await builder.addContainer(
    "worker",
    "mcr.microsoft.com/dotnet/runtime-deps:10.0"
);
await worker.withReference(outlook);

const outlookMcp = await connectorNamespace.addMcpServerConfig("outlook-mcp");
await outlookMcp.withConnector("office365", outlook, {
    operations: [
        {
            name: "GetEmailsV3",
            displayName: "Get emails"
        }
    ]
});
await outlookMcp.withAccessPolicy("developer-access", {
    objectId: "11111111-1111-1111-1111-111111111111",
    tenantId: "22222222-2222-2222-2222-222222222222",
    principalType: AzureConnectorNamespaceMcpAccessPolicyPrincipalType.User
});
```

Referencing `outlook` injects `outlook__connectorGatewayName` and `outlook__connectionName` into the workload for the Azure Connector SDK. It does not authorize the workload to use the connection. The connection access policy must identify the Microsoft Entra principal used by the deployed workload. Pass a connection-name override to `WithReference` when the consuming application uses a different configuration prefix.

After deployment, open `https://connectors.azure.com/<subscription-id>/<resource-group>/<connector-namespace-name>/overview` and authorize connections that require user consent. Aspire does not automate or store OAuth credentials.

## Security and access

* MCP connector routes require an explicit operation allow-list. Expose only the operations the application needs.
* `WithAccessPolicy` grants one explicitly identified Microsoft Entra principal access to a connection.
* `WithIdentityAccessPolicy` grants access to a user-assigned managed identity without hard-coding its principal ID.
* `WithAccessPolicy` on an MCP server configuration grants an Entra user or group permission to call its MCP endpoint. MCP access policies do not currently support managed identities or service principals.
* Do not put credentials, tokens, or other secrets in MCP descriptions or operation metadata.

Aspire provisions Connector Namespace resources through incremental ARM deployments. Removing a connection, connection access policy, MCP access policy, or MCP server configuration from the AppHost does not delete a previously deployed Azure child resource. To revoke access or retire a connection or configuration, delete it explicitly in Azure or tear down the provisioning environment.

Existing Connector Namespace resources can be referenced with the standard Azure `PublishAsExisting` and `AsExisting` APIs. Existing connection and MCP server configuration children can be marked with `AsExisting()`. Existing resources are emitted as read-only Bicep references.

When adding a new access policy beneath an existing Connector Namespace, configure the Azure deployment location to match the existing namespace location. Bicep cannot read an existing resource's location early enough to assign the child resource location automatically.

## Preview limitations

The package and service are preview features. The current integration does not support:

* Automating Connector Namespace OAuth or consent flows.
* Supplying secret-valued connection parameter sets. Create those connections outside Aspire or reference an existing connection.
* Connector triggers and event subscriptions.
* Hosted MCP servers or arbitrary MCP operation parameter schemas.

Connector names, operation IDs, and authentication requirements vary by connector and region. Verify them against the managed connector metadata before deployment.

## Configure Azure Provisioning for local development

Adding Azure resources to the Aspire application model will automatically enable development-time provisioning for Azure resources so that you don't need to configure them manually. Provisioning requires a number of settings to be available via .NET configuration. The Aspire dashboard will prompt you to set these values if they are not already configured. See [Local Azure Provisioning](https://aspire.dev/integrations/cloud/azure/local-provisioning/) for more details.

## Additional documentation

* https://aspire.dev/integrations/gallery/
* https://learn.microsoft.com/azure/connector-namespace/connector-namespace-overview
* https://learn.microsoft.com/azure/connector-namespace/create-connector-namespace-connection

## Feedback & contributing

https://github.com/microsoft/aspire
