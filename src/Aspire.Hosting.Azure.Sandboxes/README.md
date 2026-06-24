# Aspire.Hosting.Azure.Sandboxes library

Provides extension methods and resource definitions for an Aspire AppHost to configure Azure Container Apps sandboxes and connector gateway resources.

## Getting started

### Prerequisites

* An Azure subscription with access to the Azure Container Apps sandbox and connector gateway preview features.
* Azure permissions to create resource groups, sandbox groups, connector gateways, role assignments, and any referenced Azure resources.

### Install the package

In your AppHost project, install the Aspire Azure Sandboxes Hosting library with [NuGet](https://www.nuget.org):

```dotnetcli
dotnet add package Aspire.Hosting.Azure.Sandboxes
```

## Usage example

Then, in the _AppHost.cs_ file of `AppHost`, add an Azure sandbox group and publish a compute resource to it using the following methods:

```csharp
var sandboxGroup = builder.AddAzureSandboxGroup("sandboxes");

builder.AddProject<Projects.ApiService>("api")
    .WithExternalHttpEndpoints()
    .PublishAsSandbox(sandboxGroup);
```

## Connector trigger limitations

Connector gateway, connection, MCP server config, access policy, and trigger config resources can be modeled and provisioned by the AppHost. The current implementation does not run an interactive OAuth consent flow for connector connections. For connectors that require user consent, such as SharePoint, complete the connection authorization outside Aspire before relying on authenticated connector trigger delivery.

## Configure Azure Provisioning for local development

Adding Azure resources to the Aspire application model will automatically enable development-time provisioning for Azure resources so that you don't need to configure them manually. Provisioning requires a number of settings to be available via .NET configuration. The Aspire dashboard will prompt you to set these values if they are not already configured. See [Local Azure Provisioning](https://aspire.dev/integrations/cloud/azure/local-provisioning/) for more details.

> NOTE: Developers must have Owner access to the target subscription so that role assignments can be configured for the provisioned resources.

## Additional documentation

* https://learn.microsoft.com/azure/container-apps/sessions-code-interpreter
* https://learn.microsoft.com/azure/connectors/connectors-create-api-sharepointonline

## Feedback & contributing

https://github.com/microsoft/aspire
