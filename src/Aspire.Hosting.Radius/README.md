# Aspire.Hosting.Radius library

Provides extensions and resource definitions for an Aspire AppHost to publish and deploy applications to a [Radius](https://radapp.io) compute environment.

> **Preview / prototype.** This integration is an early prototype. The public API surface and the generated Bicep contract may change in future versions. Pin the integration version in `AppHost.csproj` and avoid taking dependencies on any internal types.

## Getting started

### Prerequisites

* A Kubernetes cluster (for example `kind`, `minikube`, AKS) with [Radius](https://docs.radapp.io/installation/) installed.
* The `rad` CLI on PATH. Version must match the pinned Radius Bicep extension this integration emits (currently `0.51`). Run `rad version` to check.
* `rad init` has been run against the target cluster so the workspace and environment exist.

### Install the package

In your AppHost project, install the Aspire Radius Hosting library with [NuGet](https://www.nuget.org):

```dotnetcli
dotnet add package Aspire.Hosting.Radius
```

## Usage example

Then, in the _AppHost.cs_ file of `AppHost`, add the environment:

```csharp
builder.AddRadiusEnvironment("radius");
```

`aspire publish` generates `app.bicep` plus a `bicepconfig.json` pinned to the Radius extension version, and `aspire deploy` invokes `rad deploy` against the file:

```shell
aspire publish -o radius-artifacts
aspire deploy
```

## Supported resources

* `AddContainer(...)` — published as a Radius container workload (`Radius.Compute/containers`).
* `AddProject<T>(...)` — published as a Radius container workload referencing `{name}:latest`. You must build and push the image to a registry the cluster can pull from before running `rad deploy`. Full project image build/push integration is planned.
* Selected resources with a Radius mapping (e.g. Redis, MongoDB, RabbitMQ, Dapr building blocks) emit Radius "legacy" types via the resource type mapper. Child database resources (for example `AddSqlServer("sql").AddDatabase("appdb")`) are collapsed onto the parent today.

Other Aspire resource types are not emitted; only the resources listed above appear in the generated Bicep.

## Multiple compute environments

When the model contains more than one compute environment (for example a Radius environment alongside a Kubernetes one), explicitly assign each resource to the environment that should publish it:

```csharp
var radius = builder.AddRadiusEnvironment("radius");
var k8s    = builder.AddKubernetesEnvironment("k8s");

builder.AddContainer("api", "myorg/api", "1.0")
       .WithComputeEnvironment(radius);
```

Untargeted resources surface a clear error from the core pipeline instead of being silently claimed by one environment.

## Cloud providers

Configure Azure and/or AWS cloud providers directly in the AppHost. The publisher
emits `properties.providers.azure.scope` / `providers.aws.scope` on the
`Radius.Core/environments` resource and the deploy pipeline registers
credentials via `rad credential register` before `rad deploy` runs.

```csharp
var clientSecret = builder.AddParameter("azure-sp-secret", secret: true);

builder.AddRadiusEnvironment("radius")
       .WithAzureProvider(
           subscriptionId: "00000000-0000-0000-0000-000000000000",
           resourceGroup:  "rg-radius",
           azure => azure.WithServicePrincipal(
               tenantId:     "11111111-1111-1111-1111-111111111111",
               clientId:     "22222222-2222-2222-2222-222222222222",
               clientSecret: clientSecret))
       .WithAwsProvider(
           accountId: "123456789012",
           region:    "us-west-2",
           aws => aws.WithIrsa("arn:aws:iam::123456789012:role/radius-irsa"));
```

Supported credential modes:

| Provider | Mode | Method |
|----------|------|--------|
| Azure | Service Principal | `azure.WithServicePrincipal(tenantId, clientId, clientSecret)` |
| Azure | Workload Identity | `azure.WithWorkloadIdentity(clientId, tenantId)` |
| AWS   | Access Key        | `aws.WithAccessKey(accessKeyId, secretAccessKey)` |
| AWS   | IRSA              | `aws.WithIrsa(iamRoleArn)` |

Secret material (Azure SP client secret, AWS access-key pair) must be supplied
via `builder.AddParameter(..., secret: true)`. The integration never inlines
secret values into Bicep or manifests; they are resolved at deploy time and
redacted from any logged command line.

See [specs/003-cloud-providers/quickstart.md](../../../specs/003-cloud-providers/quickstart.md) for an end-to-end walkthrough.

## Additional documentation

* https://docs.radapp.io/
* https://aspire.dev/

## Feedback & contributing

https://github.com/microsoft/aspire
