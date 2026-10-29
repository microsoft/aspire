# Azure Front Door hosting integration

Use this integration to model, configure, and orchestrate an Azure Front Door resource in an Aspire solution.

## Getting started

### Prerequisites

- An Azure subscription - [create one for free](https://azure.microsoft.com/free/)

### Add the integration

From your AppHost directory, add the `Aspire.Hosting.Azure.FrontDoor` integration with the Aspire CLI:

```bash
aspire add Aspire.Hosting.Azure.FrontDoor
```

## Usage example

In the AppHost, add an Azure Front Door resource and configure origins with either C# or TypeScript:

**C#**

```csharp
var api = builder.AddProject<Projects.Api>("api")
    .WithExternalHttpEndpoints();

var frontDoor = builder.AddAzureFrontDoor("frontdoor")
    .WithOrigin(api);
```

**TypeScript**

```typescript
const api = await builder.addNodeApp("api", "../api", "server.js")
    .withExternalHttpEndpoints();

const frontDoor = await builder.addAzureFrontDoor("frontdoor")
    .withOrigin(api);
```

## Private Link to Azure Container Apps

To let an Azure Container Apps environment accept traffic only from Azure Front Door, call `WithAzureFrontDoorPrivateLink`
on the environment and add the apps to the Front Door with `WithOrigin`. `WithAzureFrontDoorPrivateLink` is experimental. C#
callers must opt in by suppressing `ASPIREAZURE003`, as shown below.

**C#**

```csharp
#pragma warning disable ASPIREAZURE003 // WithAzureFrontDoorPrivateLink is experimental.

using Aspire.Hosting;

var builder = DistributedApplication.CreateBuilder(args);

var environment = builder.AddAzureContainerAppEnvironment("env");
var frontDoor = builder.AddAzureFrontDoor("frontdoor");
environment.WithAzureFrontDoorPrivateLink(frontDoor);

var api = builder.AddProject<Projects.Api>("api")
    .WithExternalHttpEndpoints();

frontDoor.WithOrigin(api);

builder.Build().Run();

#pragma warning restore ASPIREAZURE003
```

**TypeScript**

```typescript
import { createBuilder } from "./.aspire/modules/aspire.mjs";

const builder = await createBuilder();

const environment = await builder.addAzureContainerAppEnvironment("env");
const frontDoor = await builder.addAzureFrontDoor("frontdoor");
await environment.withAzureFrontDoorPrivateLink(frontDoor);

const api = await builder.addNodeApp("api", "../api", "server.js")
    .withHttpEndpoint({ env: "PORT" })
    .withExternalHttpEndpoints();

await frontDoor.withOrigin(api);

await builder.build().run();
```

When you publish, the environment's public network access is disabled, the Front Door profile uses the Premium SKU, and each
origin in the environment connects through a Front Door managed private endpoint. The environment must be a workload profiles
environment that Aspire provisions, not an existing or Express environment.

The private endpoint is created in the environment's region when Front Door offers Private Link there. Front Door only offers
Private Link in some [regions](https://learn.microsoft.com/azure/frontdoor/private-link#region-availability), so for an
environment in another region the private endpoint is created in the supported region of the same geography, for example
`westus2` for an environment in `westus`, at the cost of an extra network hop. A geography without a supported region, such as
Switzerland, isn't remapped and deployment fails. In that case, or to choose a different region, set the location of each
Private Link origin in `ConfigureInfrastructure`. The following AppHost sets it from a parameter. Its value is the Azure region for
the private endpoint, such as `westus2`, which you provide when you deploy:

```csharp
#pragma warning disable ASPIREAZURE003 // WithAzureFrontDoorPrivateLink is experimental.

using Aspire.Hosting;
using Azure.Provisioning.Cdn;

var builder = DistributedApplication.CreateBuilder(args);

var privateLinkLocation = builder.AddParameter("privateLinkLocation");

var environment = builder.AddAzureContainerAppEnvironment("env");
var frontDoor = builder.AddAzureFrontDoor("frontdoor")
    .ConfigureInfrastructure(infrastructure =>
    {
        // Create the parameter once, before looping over the origins.
        var location = privateLinkLocation.AsProvisioningParameter(infrastructure);

        foreach (var origin in infrastructure.GetProvisionableResources().OfType<FrontDoorOrigin>())
        {
            // Origins that aren't Private Link origins still have an empty SharedPrivateLinkResource model.
            var privateLink = origin.SharedPrivateLinkResource;
            if (!privateLink.PrivateLinkId.IsEmpty)
            {
                privateLink.PrivateLinkLocation = location;
            }
        }
    });
environment.WithAzureFrontDoorPrivateLink(frontDoor);

var api = builder.AddProject<Projects.Api>("api")
    .WithExternalHttpEndpoints();

frontDoor.WithOrigin(api);

builder.Build().Run();

#pragma warning restore ASPIREAZURE003
```

Front Door creates the connection request in the Pending state, and no traffic reaches the apps until the environment's owner
approves it, in the Azure portal or with the Azure CLI:

```bash
az network private-endpoint-connection approve --id <private-endpoint-connection-id>
```

Find the connection ID with `az network private-endpoint-connection list --type Microsoft.App/managedEnvironments --name <environment-name> --resource-group <resource-group>`.
Front Door can create more than one request, so approve each one whose description names the Front Door and the environment.
Traffic starts flowing a few minutes after approval.

## Additional documentation

* https://aspire.dev/integrations/gallery/
* https://aspire.dev/integrations/cloud/azure/azure-front-door/
* https://learn.microsoft.com/azure/frontdoor/
* https://learn.microsoft.com/azure/container-apps/how-to-integrate-with-azure-front-door

## Feedback & contributing

https://github.com/microsoft/aspire
