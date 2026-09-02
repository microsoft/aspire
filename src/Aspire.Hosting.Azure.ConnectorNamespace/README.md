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
    });

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
        });
```

**TypeScript**

```typescript
const connectorNamespace = await builder.addAzureConnectorNamespace("connectors");

const outlook = await connectorNamespace.addConnection("outlook", "office365", {
    connectionName: "office365-outlook",
    displayName: "Office 365 Outlook"
});

const outlookMcp = await connectorNamespace.addMcpServerConfig("outlook-mcp");
await outlookMcp.withConnector("office365", outlook, {
    operations: [
        {
            name: "GetEmailsV3",
            displayName: "Get emails"
        }
    ]
});
```

After deployment, open `https://connectors.azure.com/<subscription-id>/<resource-group>/<connector-namespace-name>/overview` and authorize connections that require user consent. Aspire does not automate or store OAuth credentials.

## Security and access

* MCP connector routes require an explicit operation allow-list. Expose only the operations the application needs.
* `WithAccessPolicy` grants one explicitly identified Microsoft Entra principal access to a connection.
* `WithIdentityAccessPolicy` grants access to a user-assigned managed identity without hard-coding its principal ID.
* Do not put credentials, tokens, or other secrets in MCP descriptions or operation metadata.

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
