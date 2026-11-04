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

## Schedule image cleanup

`WithPurgeTask` removes matching tags older than `ago` while always retaining the newest `keep`
matching tags per repository. All younger matching tags are also retained, so more than `keep`
tags can remain. By default, `ago` is zero, `keep` is three, and eligible untagged manifests are
also removed to reclaim storage.
Set `untagged` to `false` to remove tags only, leaving the underlying manifests and image layers.

**C#**

```csharp
registry.WithPurgeTask("0 1 * * *",
    filter: "myapp:.*",
    ago: TimeSpan.FromDays(7),
    keep: 5);
```

**TypeScript**

```typescript
await registry.withPurgeTask("0 1 * * *", {
    filter: "myapp:.*",
    ago: 7 * 24 * 60 * 60 * 1000,
    keep: 5
});
```

Manifest deletion works with a Basic registry; no Premium SKU or retention policy is required. Previously untagged manifests
in matching repositories are also eligible, so deletion can break digest-based pulls and
rollbacks. Shared image layers remain until no retained manifests reference them, and storage
usage reporting can lag behind deletion.

The task defaults to `mcr.microsoft.com/acr/acr-cli:0.21` and generates:

```text
mcr.microsoft.com/acr/acr-cli:0.21 purge --filter 'myapp:.*' --ago 7d --min-tags 5 --untagged --max-untagged-manifests 0
```

Override the image tag with `cliVersion`. The selected image must support the count-retention
flags introduced in ACR CLI 0.20:

```csharp
registry.WithPurgeTask("0 1 * * *", filter: "myapp:.*", cliVersion: "0.20");
```

```typescript
await registry.withPurgeTask("0 1 * * *", { filter: "myapp:.*", cliVersion: "0.20" });
```

`keep` sets `--min-tags`, not `--keep` or `--max-tags`. The minimum protects the newest matching
tags even when all tags are older than `ago`; other tags are eligible only when older than `ago`.
When `ago` is omitted or zero, the task emits `--ago 0d`, making all tags outside the minimum
eligible. The C# `ago` parameter accepts a `TimeSpan`; the TypeScript option accepts milliseconds.

Untagged cleanup is independent of tag retention: `--max-untagged-manifests 0` retains no extra
eligible untagged manifests, including ones younger than `ago`. Use `untagged: false` to disable it.

Locked artifacts and dependencies of retained images remain protected, so these limits do not
guarantee that only `keep` manifests or `keep` deployable application versions remain. See
[ACR purge behavior](https://learn.microsoft.com/azure/container-registry/container-registry-auto-purge).

## Additional documentation

* https://aspire.dev/integrations/gallery/
* https://aspire.dev/integrations/cloud/azure/azure-container-registry/azure-container-registry-host/
* https://aspire.dev/app-host/container-registry/
* https://learn.microsoft.com/azure/container-registry/

## Feedback & contributing

https://github.com/microsoft/aspire
