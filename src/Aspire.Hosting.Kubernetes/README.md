# Kubernetes hosting integration

Use this integration to model, configure, and deploy Aspire compute resources to Kubernetes.

## Getting started

### Prerequisites

- [Helm](https://helm.sh/docs/intro/install/) **v4.2.0 or later** on your `PATH`.

Aspire shells out to `helm upgrade --install` to deploy the generated chart and validates the Helm version up front, so missing or older installs produce a clear actionable error instead of cryptic flag failures.

### Add the integration

From your AppHost directory, add the `Aspire.Hosting.Kubernetes` integration with the Aspire CLI:

```bash
aspire add Aspire.Hosting.Kubernetes
```

## Usage example

In the AppHost, add the environment:

**C#**

```csharp
builder.AddKubernetesEnvironment("k8s");
```

**TypeScript**

```typescript
await builder.addKubernetesEnvironment("k8s");
```

### Volumes

Use a target-neutral volume when the Kubernetes environment's default storage policy is sufficient:

**C#**

```csharp
builder.AddProject<Projects.Api>("api")
    .WithVolume("data", "/data", env: "DATA_PATH");
```

**TypeScript**

```typescript
const api = await builder.addNodeApp("api", "../api", "server.js");
await api.withVolume("/data", "data", "DATA_PATH");
```

Projects and executables receive a workload-scoped Aspire store directory in run mode. The directory is reused across AppHost runs regardless of whether the process has a session or persistent lifetime. When published, the volume uses the Kubernetes environment's `DefaultStorageType`, which is `emptyDir` — storage lives for the lifetime of the pod and is lost when the pod restarts or is rescheduled. `DATA_PATH` contains `/data`. For storage that outlives the pod, use a [persistent volume](#persistent-volumes) instead.

### Persistent volumes

Add a persistent volume and expose its effective path through an environment variable:

**C#**

```csharp
var k8s = builder.AddKubernetesEnvironment("k8s");
var data = k8s.AddPersistentVolume("data")
    .WithCapacity("20Gi");

builder.AddProject<Projects.Api>("api")
    .WithPersistentVolume(data, "/data", env: "DATA_PATH");
```

**TypeScript**

```typescript
const k8s = await builder.addKubernetesEnvironment("k8s");
const data = await k8s.addPersistentVolume("data");
await data.withCapacity("20Gi");

const api = await builder.addNodeApp("api", "../api", "server.js");
await api.withKubernetesPersistentVolumeMount(data, "/data", { env: "DATA_PATH" });
```

When a project or executable runs locally, `DATA_PATH` points to a persistent directory in the AppHost's Aspire store. That store is normally under the AppHost intermediate-output directory, so cleaning build outputs can remove the local data. Local containers use a worktree-scoped container volume instead, provided the mount names an environment variable. Mounts that do not name one keep the persistent volume's own name for the local container volume, so data written by an earlier version of the AppHost stays attached. A single persistent-volume resource cannot be shared between local containers and local projects or executables because those execution types cannot use one backing store reliably.

When published or deployed, `DATA_PATH` contains `/data`. Applications can therefore use the same environment variable in both environments. The `isReadOnly` mount option is enforced after deployment, but Aspire cannot make a directory read-only for a process running directly on the host.

```shell
aspire publish -o k8s-artifacts
```

## Additional documentation

https://aspire.dev/integrations/gallery/
https://aspire.dev/integrations/compute/kubernetes/

## Feedback & contributing

https://github.com/microsoft/aspire
