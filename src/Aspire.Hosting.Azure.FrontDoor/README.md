# Azure Front Door hosting integration

Use this integration to model, configure, and orchestrate an Azure Front Door resource in an Aspire solution.

## Getting started

### Prerequisites

- An Azure subscription - [create one for free](https://azure.microsoft.com/free/)

### Add the integration

From your AppHost directory, add the `Aspire.Hosting.Azure.FrontDoor` integration with the Aspire CLI:

```bash
aspire add Aspire.Hosting.Azure.FrontDoor
aspire add Aspire.Hosting.Dotnet
```

## Usage example

In the AppHost, add an Azure Front Door resource and configure origins with either C# or TypeScript:

**C#**

```csharp
var api = builder.AddDotnetProject("api", "../Api/Api.csproj")
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

var api = builder.AddDotnetProject("api", "../Api/Api.csproj")
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

var api = builder.AddDotnetProject("api", "../Api/Api.csproj")
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

## Private Link to Azure App Service

The same API supports an Azure App Service environment:

**C#**

```csharp
#pragma warning disable ASPIREAZURE003 // WithAzureFrontDoorPrivateLink is experimental.

var environment = builder.AddAzureAppServiceEnvironment("env");
var frontDoor = builder.AddAzureFrontDoor("frontdoor");
environment.WithAzureFrontDoorPrivateLink(frontDoor);

var api = builder.AddProject<Projects.Api>("api")
    .WithExternalHttpEndpoints();
var web = builder.AddProject<Projects.Web>("web")
    .WithExternalHttpEndpoints();
frontDoor.WithOrigin(api).WithOrigin(web);

#pragma warning restore ASPIREAZURE003
```

**TypeScript**

```typescript
const environment = await builder.addAzureAppServiceEnvironment("env");
const frontDoor = await builder.addAzureFrontDoor("frontdoor");
await environment.withAzureFrontDoorPrivateLink(frontDoor);

const api = await builder.addProject("api", "../Api/Api.csproj")
    .withExternalHttpEndpoints();
const web = await builder.addProject("web", "../Web/Web.csproj")
    .withExternalHttpEndpoints();
await frontDoor.withOrigin(api).withOrigin(web);
```

Add both the App Service and Front Door integrations to the AppHost. Each App Service origin connects to its own website
(`Microsoft.Web/sites`, group ID `sites`), not to the App Service plan. The Front Door profile uses Premium, and the private
endpoint region follows the same region mapping described above, using the website's location.

`aspire deploy` deploys the websites before Front Door and resolves their resource IDs and locations automatically.
`aspire publish` writes website modules separately from `main.bicep`; the latter exposes the required website IDs and
locations as input parameters. When applying the published assets yourself, deploy the environment and website modules
first, then supply those website outputs to the Front Door infrastructure.

Public network access is disabled on **all workload websites** in the connected environment, including websites not added
as Front Door origins. The separately provisioned Aspire dashboard is unchanged; disable it with `WithDashboard(false)`
if it is not needed. Existing App Service plans are supported, since Aspire still provisions the websites.
[Front Door Private Link does not support App Service deployment slots](https://learn.microsoft.com/azure/frontdoor/standard-premium/how-to-enable-private-link-web-app),
so combining it with `WithDeploymentSlot` fails at publish time.

After `aspire deploy`, approve the pending connection on **each website**, not on the plan:

```bash
az network private-endpoint-connection list --type Microsoft.Web/sites --name <website-name> --resource-group <resource-group>
az network private-endpoint-connection approve --id <private-endpoint-connection-id>
```

Approve every request whose description names this Front Door and environment. Allow a few minutes for Front Door to
propagate the approval. Direct requests to the website's `*.azurewebsites.net` URL are denied; use its Front Door URL.

Unlike Container Apps, App Service does not provide environment-local ingress between apps. `WithReference` continues
to use the website hostname, which is not publicly accessible in this configuration. Service-to-service callers must use
the Front Door URLs (available through `frontDoor.Resource.GetEndpointUrl("api")`) or configure their own private
networking and DNS. Passing a Front Door output back into one of its origins creates a deployment dependency cycle;
configure such URLs separately after deployment.

## Additional documentation

* https://aspire.dev/integrations/gallery/
* https://aspire.dev/integrations/cloud/azure/azure-front-door/
* https://learn.microsoft.com/azure/frontdoor/
* https://learn.microsoft.com/azure/container-apps/how-to-integrate-with-azure-front-door
* https://learn.microsoft.com/azure/frontdoor/standard-premium/how-to-enable-private-link-web-app

## Feedback & contributing

https://github.com/microsoft/aspire
