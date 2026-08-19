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

## Global entry point over regional stamps

Deploy one application to several Azure regions and put a single global hostname in front of all of them.
Bind the application to more than one compute environment with `WithComputeEnvironments`, and Front Door
creates one origin per region inside a single origin group, health-probing and load-balancing across them:

```csharp
var eastus = builder.AddAzureContainerAppEnvironment("aca-eastus").WithLocation("eastus");
var westeu = builder.AddAzureContainerAppEnvironment("aca-westeu").WithLocation("westeurope");

var api = builder.AddProject<Projects.Api>("api")
    .WithExternalHttpEndpoints()
    .WithComputeEnvironments(eastus, westeu);

builder.AddAzureFrontDoor("frontdoor")
    .WithOrigin(api);
```

Use `WithOriginGroup` to control how traffic is distributed, how origins are health-probed, and which custom
domain serves the application:

```csharp
builder.AddAzureFrontDoor("frontdoor")
    .WithOriginGroup(api, g => g
        .WithRouting(FrontDoorOriginRouting.LatencyBased)
        .WithHealthProbe("/health", FrontDoorHealthProbeProtocol.Https, TimeSpan.FromSeconds(30))
        .WithCustomDomain("www.contoso.com"));
```

For an active/passive topology, prefer one region and fail over to another:

```csharp
builder.AddAzureFrontDoor("frontdoor")
    .WithOriginGroup(api, g => g
        .WithRouting(FrontDoorOriginRouting.Failover)
        .WithStampPriority(eastus, 1)
        .WithStampPriority(westeu, 2));
```

Notes:

* Health probing is what makes regional failover work. Probe a path that reflects the health of the whole
  stamp rather than one that always returns success.
* The generated `*.azurefd.net` hostname is not known until deployment completes, so an application cannot be
  told its own public address through it — that would create a Bicep module cycle, because Front Door already
  depends on the application's host address. Use `WithCustomDomain` when the application needs to know its
  public hostname.
* A custom domain does not serve traffic until ownership is proven. The required DNS TXT token is emitted as
  the `{origin}_customDomainValidationToken` Bicep output.

## Additional documentation

* https://aspire.dev/integrations/gallery/
* https://aspire.dev/integrations/cloud/azure/azure-front-door/
* https://learn.microsoft.com/azure/frontdoor/

## Feedback & contributing

https://github.com/microsoft/aspire
