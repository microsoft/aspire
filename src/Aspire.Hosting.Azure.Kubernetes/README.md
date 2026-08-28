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

myService.WithPersistentVolume(data, "/data");
```

**TypeScript**

```typescript
const data = await aks.addPersistentVolume("data");
await data.withCapacity("20Gi");

await myService.withKubernetesPersistentVolumeMount(data, "/data");
```

When no storage class is specified, the generated claim uses the cluster's default storage class. A standard AKS cluster dynamically provisions an Azure managed disk. To request Premium SSD storage explicitly, call `WithStorageClass("managed-csi-premium")` in C# or `withStorageClass("managed-csi-premium")` in TypeScript.

#### Azure file shares

Use a statically provisioned Azure file share for shared, persistent storage. This
creates one managed identity per persistent volume and federates each consuming
workload's Kubernetes service account to it. It does not create a Kubernetes Secret,
grant storage access to the kubelet identity, or use a storage account key.

**C#**

```csharp
var files = builder.AddAzureStorage("storage").AddFiles("files");
var share = files.AddFileShare("media-share", "media");

var media = aks.AddPersistentVolume("media-volume")
    .WithAzureFileShare(share)
    .WithCapacity("100Gi");

myService.WithPersistentVolume(media, "/srv/media");
```

**TypeScript**

```typescript
const storage = await builder.addAzureStorage("storage");
const files = await storage.addFiles("files");
const share = await files.addFileShare("media-share", { fileShareName: "media" });

const media = await aks.addPersistentVolume("media-volume");
await media.withAzureFileShare(share);
await media.withCapacity("100Gi");

await myService.withKubernetesPersistentVolumeMount(media, "/srv/media");
```

Workload identity mounts require Azure Files CSI driver version 1.35.0 or later on
Linux nodes. Aspire grants the volume identity a data-plane-only role at storage-account
scope; the role has no Azure Resource Manager control-plane permissions but applies to every
file share in that account. Use separate storage accounts when volumes require independent
data-access boundaries. Storage accounts created by Aspire enable SMB OAuth and disable shared
key authentication. Existing storage accounts and file shares are not modified and must already
meet those authentication requirements.

Azure infrastructure deployments are incremental. Removing a persistent volume or retargeting
it to another storage account does not remove its previous managed identity or role assignment.
Remove those resources explicitly, or run `aspire destroy` when the application owns the
deployment resource group.

## Additional documentation

* https://aspire.dev/integrations/gallery/
* https://aspire.dev/integrations/cloud/azure/aks/
* https://learn.microsoft.com/azure/aks/

## Feedback & contributing

https://github.com/microsoft/aspire
