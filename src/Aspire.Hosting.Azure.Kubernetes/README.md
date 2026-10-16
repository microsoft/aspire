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

### Existing clusters

Use the shared Azure existing-resource APIs to deploy to a pre-provisioned AKS cluster:

```csharp
var clusterName = builder.AddParameter("cluster-name");
var resourceGroup = builder.AddParameter("cluster-resource-group");
var subscription = builder.AddParameter("cluster-subscription");

var aks = builder.AddAzureKubernetesEnvironment("aks")
    .AsExistingInResourceGroup(clusterName, resourceGroup, subscription);
```

Use `AsExisting(clusterName, null)` when the cluster is in the application's Azure resource group. `PublishAsExisting` applies only when publishing/deploying; `RunAsExisting` applies only in run mode.

Aspire references the cluster without changing its node pools, networking, or add-ons, and does not assign workloads to an automatically created `workload` node pool. Cluster-creation settings do not reconfigure an existing cluster. Explicit node-pool affinity must name a pool that already exists. The cluster must have a kubelet managed identity for registry access; workloads using Azure workload identity also require OIDC and workload identity to be enabled beforehand. Any ingress add-ons used by the application must already be installed and configured.

Aspire still provisions the default container registry, grants the cluster's kubelet identity `AcrPull` access, and creates workload identity federated credentials when needed. The deploying identity needs permission to create these supporting resources and role assignments, retrieve AKS credentials, and deploy Kubernetes workloads. `aspire destroy` removes the application's workloads and deletes the application's Azure resource group. Keep the existing cluster in a separate resource group if it must survive `aspire destroy`.

### Cluster tier

AKS clusters use the **Standard** control-plane tier by default, providing an uptime SLA for production workloads. Standard includes a cluster-management charge in addition to the underlying Azure resource costs. See [AKS pricing tiers](https://learn.microsoft.com/azure/aks/free-standard-pricing-tiers) for current details.

Applying updated infrastructure to an existing Aspire-managed cluster changes its tier to Standard unless explicitly overridden. Upgrading the integration package alone does not change a deployed cluster.

To use the Free tier for development or testing, customize the infrastructure in a C# AppHost:

```csharp
using Azure.Provisioning.ContainerService;

var aks = builder.AddAzureKubernetesEnvironment("aks")
    .ConfigureInfrastructure(infrastructure =>
    {
        var cluster = infrastructure.GetProvisionableResources()
            .OfType<ContainerServiceManagedCluster>()
            .Single();

        cluster.Sku.Tier = ManagedClusterSkuTier.Free;
    });
```

The control-plane tier does not change node VM sizes or provide an application availability guarantee.

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
