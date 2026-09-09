# Azure Kubernetes Service hosting integration

Use this integration to model, configure, and orchestrate an Azure Kubernetes Service (AKS) environment in an Aspire solution.

## Getting started

### Prerequisites

- An Azure subscription - [create one for free](https://azure.microsoft.com/free/)
- [Helm](https://helm.sh/docs/intro/install/) **v4.2.0 or later** on your `PATH`.

Aspire shells out to `helm upgrade --install` to deploy the generated chart and any `AddHelmChart(...)` releases, and validates the Helm version up front so missing or older installs produce a clear actionable error instead of cryptic flag failures.

### Add the integration

From your AppHost directory, add the `Aspire.Hosting.Azure.Kubernetes` integration with the Aspire CLI:

```bash
aspire add Aspire.Hosting.Azure.Kubernetes
```

## Usage example

In the AppHost, add an AKS environment and deploy services to it:

**C#**

```csharp
var aks = builder.AddAzureKubernetesEnvironment("aks");

var myService = builder.AddProject<Projects.MyService>()
    .WithComputeEnvironment(aks);
```

**TypeScript**

```typescript
const aks = await builder.addAzureKubernetesEnvironment("aks");

const myService = await builder.addNodeApp("myService", "../my-service", "server.js")
    .withComputeEnvironment(aks);
```

### Persistent volumes

Add a persistent volume to the AKS environment and mount it into a workload:

**C#**

```csharp
var data = aks.AddPersistentVolume("data")
    .WithCapacity("20Gi");

myService.WithPersistentVolume(data, "/data", env: "DATA_PATH");
```

**TypeScript**

```typescript
const data = await aks.addPersistentVolume("data");
await data.withCapacity("20Gi");

await myService.withKubernetesPersistentVolumeMount(data, "/data", { env: "DATA_PATH" });
```

When a project or executable runs locally, `DATA_PATH` points to a persistent directory in the AppHost's Aspire store. That store is normally under the AppHost intermediate-output directory, so cleaning build outputs can remove the local data. Local containers use a worktree-scoped container volume instead when the mount names an environment variable, and one persistent-volume resource cannot be shared between local containers and local projects or executables.

In AKS, `DATA_PATH` contains `/data`, the mounted volume path. When no storage class is specified, the generated claim uses the cluster's default storage class. A standard AKS cluster dynamically provisions an Azure managed disk. To request Premium SSD storage explicitly, call `WithStorageClass("managed-csi-premium")` in C# or `withStorageClass("managed-csi-premium")` in TypeScript.

## Additional documentation

* https://aspire.dev/integrations/gallery/
* https://aspire.dev/integrations/cloud/azure/aks/
* https://learn.microsoft.com/azure/aks/

## Feedback & contributing

https://github.com/microsoft/aspire
