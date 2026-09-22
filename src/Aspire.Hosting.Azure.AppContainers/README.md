# Azure Container Apps hosting integration

Use this integration to model, configure, and orchestrate Azure Container Apps environments and deploy compute resources in an Aspire solution.

## Getting started

### Prerequisites

- An Aspire AppHost and a running container runtime, such as Docker, for building and running container images.
- For deployment, an Azure subscription with permission to create resources and assign roles, and the Azure CLI authenticated with `az login`.

### Add the integration

From your AppHost directory, add the `Aspire.Hosting.Azure.AppContainers` integration with the Aspire CLI:

```bash
aspire add Aspire.Hosting.Azure.AppContainers
```

## Usage example

Then, in the AppHost, add an Azure Container Apps environment and a web app to deploy to it.
The example uses the core `AddDockerfile` API and assumes a `web` subdirectory of the AppHost directory contains a Dockerfile for an HTTP app listening on port `8080`.

**C#**

```csharp
using Aspire.Hosting;

var builder = DistributedApplication.CreateBuilder(args);

builder.AddAzureContainerAppEnvironment("env");

builder.AddDockerfile("web", "web")
    .WithHttpEndpoint(targetPort: 8080)
    .WithExternalHttpEndpoints();

builder.Build().Run();
```

**TypeScript**

```typescript
import { createBuilder } from './.aspire/modules/aspire.mjs';

const builder = await createBuilder();

await builder.addAzureContainerAppEnvironment("env");

await builder.addDockerfile("web", "web")
    .withHttpEndpoint({ targetPort: 8080 })
    .withExternalHttpEndpoints();

await builder.build().run();
```

With one environment, supported compute resources are assigned to it automatically. `PublishAsAzureContainerApp` is only needed to customize the generated Container App.
HTTP endpoints are internal by default; `WithExternalHttpEndpoints` opts this web app into public ingress.

## Publish and deploy

- `aspire publish` generates Bicep and parameterized deployment artifacts without provisioning Azure resources.
- `aspire deploy` resolves Azure settings and parameters, provisions the environment and its default container registry, builds and pushes images, and deploys the app.

Both commands write artifacts to the AppHost's `aspire-output` directory by default. Interactive deployment can prompt for the subscription, location, and resource group.
Local runs do not provision the Container Apps environment. Deployed apps pull images using managed identity; keep sensitive configuration in secret parameters or Azure Key Vault references.

To clean up, use `aspire destroy`. **This deletes the entire deployment resource group, including resources not created by Aspire.**

## Additional documentation

* https://aspire.dev/integrations/gallery/
* https://aspire.dev/deployment/azure/container-apps/
* https://aspire.dev/integrations/cloud/azure/configure-container-apps/
* https://learn.microsoft.com/azure/container-apps/

## Feedback & contributing

https://github.com/microsoft/aspire
