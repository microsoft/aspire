# Azure Container Registry hosting integration

Use this integration to model, configure, and orchestrate an Azure Container Registry for container images in an Aspire solution.

## Getting started

### Prerequisites

- An Aspire AppHost and a running container runtime, such as Docker, for building and pushing container images.
- For deployment, an Azure subscription with permission to create resources and assign roles, and the Azure CLI authenticated with `az login`.

### Add the integration

From your AppHost directory, add the `Aspire.Hosting.Azure.ContainerRegistry` integration with the Aspire CLI:

```bash
aspire add Aspire.Hosting.Azure.ContainerRegistry
```

The example also uses the Azure Container Apps deployment target:

```bash
aspire add Aspire.Hosting.Azure.AppContainers
```

## Usage example

Then, in the AppHost, add a registry and attach it to an Azure Container Apps environment.
The example uses the core `AddDockerfile` API and assumes a `worker` subdirectory of the AppHost directory contains a Dockerfile for a background application.

**C#**

```csharp
using Aspire.Hosting;

var builder = DistributedApplication.CreateBuilder(args);

var registry = builder.AddAzureContainerRegistry("registry");

builder.AddAzureContainerAppEnvironment("env")
    .WithAzureContainerRegistry(registry);

builder.AddDockerfile("worker", "worker");

builder.Build().Run();
```

**TypeScript**

```typescript
import { createBuilder } from './.aspire/modules/aspire.mjs';

const builder = await createBuilder();

const registry = await builder.addAzureContainerRegistry("registry");
const environment = await builder.addAzureContainerAppEnvironment("env");
await environment.withAzureContainerRegistry(registry);

await builder.addDockerfile("worker", "worker");

await builder.build().run();
```

`WithAzureContainerRegistry` selects the registry used by the environment's deployment pipeline and managed-identity image pulls; it is not a runtime connection reference.
Without an explicit registry, the Container Apps environment creates a default registry.

The registry is deployment infrastructure and is not provisioned during local runs. `aspire publish` generates its Bicep; `aspire deploy` provisions it and pushes the application's images as part of deployment.
See the [Container Apps deployment guide](https://aspire.dev/deployment/azure/container-apps/) for Azure settings, deployment, and cleanup.

## Additional documentation

* https://aspire.dev/integrations/gallery/
* https://aspire.dev/integrations/cloud/azure/azure-container-registry/azure-container-registry-host/
* https://aspire.dev/app-host/container-registry/
* https://learn.microsoft.com/azure/container-registry/

## Feedback & contributing

https://github.com/microsoft/aspire
