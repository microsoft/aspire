# Azure Container Apps Sandboxes hosting integration

Use this integration to model, configure, and orchestrate Azure Container Apps Sandboxes and Azure Connector Namespace resources.

## Getting started

### Prerequisites

* An Azure subscription and region with Azure Container Apps Sandboxes and Connector Namespace preview access.
* Permission to create sandbox groups, Connector Namespace resources, Azure Container Registry resources, child resources, access policies, and scoped role assignments.
* Docker or Podman for building and inspecting Linux/amd64 OCI images.
* A user account authorized to complete any connector-specific OAuth or consent flow.

The integration grants the deployment identity the **Container Apps SandboxGroup Data Owner** role on a sandbox group that it provisions. When using an existing sandbox group, grant that role to the deployment identity before deploying.

### Install the package

In your AppHost project, install the Azure Container Apps Sandboxes hosting integration:

```bash
aspire add Aspire.Hosting.Azure.Sandboxes
```

## Usage example

Then, in the _AppHost.cs_ file of `AppHost`, add an Azure sandbox group and publish a compute resource to it using the following methods:

```csharp
var sandboxGroup = builder.AddAzureSandboxGroup("sandboxes");

builder.AddProject<Projects.ApiService>("api")
    .WithHttpEndpoint(name: "http", targetPort: 8080)
    .WithExternalHttpEndpoints()
    .PublishAsAzureSandbox(sandboxGroup, new AzureSandboxOptions
    {
        Tier = AzureSandboxTier.Medium,
        AutoSuspendEnabled = true,
        AutoSuspendInterval = 900,
        AutoSuspendMode = "Disk",
        Endpoints =
        [
            new AzureSandboxEndpointOptions
            {
                Name = "http",
                Anonymous = true
            }
        ]
    });
```

Endpoints are not exposed unless they are marked external. External endpoints require an explicit `Anonymous = true` opt-in for anonymous access. Sandbox egress is configured with full inspection and deny-by-default behavior.

Images are resolved to immutable Linux/amd64 digests before import. Deployment state stores sandbox, disk-image, and endpoint identifiers, but does not persist registry credentials.

### Connector Namespace, MCP, and trigger example

Connector Namespace is the product name for the preview Azure resource whose ARM type remains `Microsoft.Web/connectorGateways`.

```csharp
var connectorNamespace = builder.AddAzureConnectorGateway("connectors");

var outlook = connectorNamespace.AddConnection(
    "outlook",
    "office365",
    new AzureConnectorGatewayConnectionOptions
    {
        ConnectionName = "office365-outlook",
        DisplayName = "Office 365 Outlook"
    });

var outlookMcp = connectorNamespace.AddMcpServerConfig(
    "outlook-mcp",
    new AzureConnectorGatewayMcpServerConfigOptions
    {
        Description = "Allow-listed Outlook tools."
    });

outlookMcp.WithConnector(
    "office365",
    outlook,
    new AzureConnectorGatewayMcpConnectorOptions
    {
        Operations =
        [
            new AzureConnectorGatewayMcpOperationOptions
            {
                Name = "GetEmailsV3",
                DisplayName = "Get emails"
            }
        ]
    });

var sandboxGroup = builder.AddAzureSandboxGroup("sandboxes");
var listener = builder.AddProject<Projects.Listener>("listener")
    .WithHttpEndpoint(name: "http", targetPort: 8080)
    .WithExternalHttpEndpoints()
    .PublishAsAzureSandbox(sandboxGroup);

outlook.AddTriggerConfig(
    "new-email",
    "OnNewEmailV3",
    listener.GetEndpoint("http"),
    new AzureConnectorGatewayTriggerOptions
    {
        CallbackPath = "/webhook",
        Parameters =
        [
            new AzureConnectorGatewayTriggerParameter
            {
                Name = "folderPath",
                Value = "Inbox"
            }
        ]
    });
```

The equivalent TypeScript AppHost shape is:

```typescript
const connectorNamespace = await builder.addAzureConnectorGateway("connectors");

const outlook = await connectorNamespace.addConnection(
    "outlook",
    "office365",
    {
        connectionName: "office365-outlook",
        displayName: "Office 365 Outlook"
    });

const outlookMcp = await connectorNamespace.addMcpServerConfig("outlook-mcp", {
    description: "Allow-listed Outlook tools."
});

await outlookMcp.withConnector("office365", outlook, {
    operations: [
        {
            name: "GetEmailsV3",
            displayName: "Get emails"
        }
    ]
});

const sandboxGroup = await builder.addAzureSandboxGroup("sandboxes");
const listener = await builder
    .addContainer("listener", "example/listener:latest")
    .withHttpEndpoint({ name: "http", targetPort: 8080 })
    .withExternalHttpEndpoints();

await listener.publishAsAzureSandbox(sandboxGroup);

await outlook.addTriggerConfig(
    "new-email",
    "OnNewEmailV3",
    await listener.getEndpoint("http"),
    {
        callbackPath: "/webhook",
        parameters: [
            {
                name: "folderPath",
                value: "Inbox"
            }
        ]
    });
```

After deployment, open `https://connectors.azure.com/<subscription-id>/<resource-group>/<connector-namespace-name>/overview` and authorize connections that require user consent. Aspire does not automate or store OAuth credentials.

## Security and access

* MCP connector routes require an explicit operation allow-list. Expose only the operations the application needs, especially for user-delegated email, files, Teams, CRM, and other business data.
* A sandbox trigger automatically creates a connection access policy for the Connector Namespace system-assigned identity.
* The trigger callback port remains non-anonymous. The Connector Namespace principal and tenant IDs are added to the sandbox port's Microsoft Entra allow-list, the port uses on-demand activation, and trigger delivery uses the `https://auth.adcproxy.io/` token audience.
* Port-trigger delivery does not require granting the Connector Namespace the broad SandboxGroup Data Owner role. That role is required for sandbox command/data-plane operations, which this trigger API does not expose.
* `WithAccessPolicy` grants one explicitly identified Microsoft Entra principal access to a connection. `WithIdentityAccessPolicy` uses a user-assigned managed identity output without hard-coding its principal ID. Neither API completes downstream OAuth consent.
* Do not put credentials, tokens, or other secrets in trigger parameters, MCP descriptions, or operation metadata.

Existing Connector Namespace resources can be referenced with the standard Azure `PublishAsExisting`/`AsExisting` APIs. Existing connection and MCP server configuration children can be marked with `AsExisting()`. Existing resources are emitted as read-only Bicep references; adding an access policy or a new sibling child remains an explicit provisioning operation. `AddTriggerConfig` rejects an existing connection because trigger creation requires a new Connector Namespace identity access policy; manage that access policy and trigger outside Aspire when the connection must remain existing.

## Preview limitations

The package and service are preview features. The current integration does not support:

* Volumes, snapshots, shell/file APIs, or interactive lifecycle commands.
* TCP ports, private service discovery, or cross-group endpoint references.
* Windows, ARM64, or arbitrary registry credentials.
* Runtime sandbox URLs as first-pass ARM/Bicep inputs.
* Automating Connector Namespace OAuth or consent flows.
* Supplying secret-valued connection parameter sets. Create those connections outside Aspire or reference an existing connection.
* Structured trigger parameter values such as recurrence objects or request bodies.
* Hosted MCP servers or arbitrary MCP operation parameter schemas.

Connector names, operation IDs, trigger parameters, and authentication requirements vary by connector and region. Verify them against the managed connector metadata before deployment. Connector connections can be provisioned before consent, but they are not usable until their authorization status is healthy.

Live trigger deployment tests are gated because Connector Namespace preview enrollment and an interactive downstream OAuth consent cannot be represented safely as unattended CI credentials. The package retains buildable C#, Bicep snapshot, and TypeScript AppHost coverage for the deployment shape.

## Configure Azure Provisioning for local development

Adding Azure resources to the Aspire application model will automatically enable development-time provisioning for Azure resources so that you don't need to configure them manually. Provisioning requires a number of settings to be available via .NET configuration. The Aspire dashboard will prompt you to set these values if they are not already configured. See [Local Azure Provisioning](https://aspire.dev/integrations/cloud/azure/local-provisioning/) for more details.

> NOTE: Developers must have Owner access to the target subscription so that role assignments can be configured for the provisioned resources.

## Additional documentation

* https://aspire.dev/integrations/gallery/
* https://learn.microsoft.com/azure/container-apps/sandboxes-overview
* https://learn.microsoft.com/azure/connector-namespace/connector-namespace-overview
* https://learn.microsoft.com/azure/connector-namespace/create-connector-namespace-connection

## Feedback & contributing

https://github.com/microsoft/aspire
