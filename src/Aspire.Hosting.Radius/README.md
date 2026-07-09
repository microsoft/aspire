# Radius hosting integration

Use this integration to publish and deploy an Aspire AppHost's applications to a [Radius](https://radapp.io) compute environment.

`AddRadiusEnvironment` is an Aspire **compute environment**, the same kind of building block as
`AddKubernetesEnvironment`, `AddDockerComposeEnvironment`, and `AddAzureContainerAppEnvironment`.
Add it to your AppHost, keep your existing resource graph unchanged, and target it with the standard
`aspire publish` / `aspire deploy` lifecycle — Radius becomes just another target you
deploy to, with no changes to how you declare `AddContainer`, `AddProject`, `AddRedis`, and friends.
Radius participates only at publish/deploy time; `aspire run` continues to run your app locally as usual.

> **Preview / prototype.** This integration is an early prototype. The public API surface and the generated Bicep contract may change in future versions. Pin the integration version in `AppHost.csproj` and avoid taking dependencies on any internal types.

This README is layered by intent:

* **Getting started** — the happy path: add the environment, run, publish, deploy.
* **Deploying to a cloud** — Azure/AWS providers and credentials.
* **Production & platform features** — secret stores, multiple resource groups, resource/recipe customization.
* **Reference** — supported resources, diagnostics, and known limitations.

## Getting started

### Prerequisites

* **Radius v0.59.0 or later.** This integration is developed and verified against Radius **v0.59.0** and up; the generated Bicep (resource types, `secretStores`, and `recipeConfig`) targets the schemas shipped in that release. Older Radius versions are not supported.
* A Kubernetes cluster (for example `kind`, `minikube`, AKS) with [Radius](https://docs.radapp.io/installation/) installed.
* The `rad` CLI on PATH. Version must match the pinned Radius Bicep extension this integration emits (currently `0.59`). Run `rad version` to check.
* `rad init` has been run against the target cluster so the workspace and environment exist.

### Add the integration

From your AppHost directory, add the `Aspire.Hosting.Radius` integration with the Aspire CLI:

```bash
aspire add Aspire.Hosting.Radius
```

## Quick start

In the _AppHost.cs_ file of `AppHost`, add the environment:

**C#**

```csharp
builder.AddRadiusEnvironment("radius");
```

**TypeScript**

```typescript
await builder.addRadiusEnvironment("radius");
```

That single line is all you add — your existing resource declarations stay the same. The standard
Aspire lifecycle still works; Radius participates only in publish and deploy:

| Command | What happens |
|---------|--------------|
| `aspire run` | Runs your app locally as usual. Radius does no run-mode wiring — the Radius environment is inert during local development and takes effect only at publish/deploy. |
| `aspire publish` | Generates `app.bicep` plus a `bicepconfig.json` pinned to the Radius extension version. |
| `aspire deploy` | Invokes `rad deploy` against the generated Bicep — no direct `rad` knowledge needed for the happy path. |

Publish and deploy:

```shell
aspire publish -o radius-artifacts
aspire deploy
```

### Local development with `aspire run`

Radius is a publish/deploy-only target: `aspire run` builds and runs your app locally exactly as it
would without Radius, using the normal Aspire dashboard for your resources. The Radius environment
does not attach annotations or alter your resources during local development — Radius wiring happens
when you `aspire publish` / `aspire deploy`. You iterate locally as usual, then publish/deploy the
same application resources to a cluster.

### Multiple compute environments

When the model contains more than one compute environment (for example a Radius environment alongside a Kubernetes one), explicitly assign each resource to the environment that should publish it:

```csharp
var radius = builder.AddRadiusEnvironment("radius");
var k8s    = builder.AddKubernetesEnvironment("k8s");

builder.AddContainer("api", "myorg/api", "1.0")
       .WithComputeEnvironment(radius);
```

Untargeted resources surface a clear error from the core pipeline instead of being silently claimed by one environment.

## Deploying to a cloud

Everything above works against a plain Kubernetes cluster with Radius installed. To target Azure
and/or AWS resources, configure the providers in the AppHost.

### Cloud providers

Configure Azure and/or AWS cloud providers directly in the AppHost. The publisher
emits the provider configuration on the `Radius.Core/environments` resource using the
native schema's discrete fields — `properties.providers.azure.subscriptionId` /
`properties.providers.azure.resourceGroupName` for Azure and
`properties.providers.aws.accountId` / `properties.providers.aws.region` for AWS
(the legacy `Applications.Core/environments` schema instead used a single `scope`
path) — and the deploy pipeline registers credentials via `rad credential register`
before `rad deploy` runs.

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
| Azure | Workload Identity | `azure.WithWorkloadIdentity(tenantId, clientId)` |
| AWS   | Access Key        | `aws.WithAccessKey(accessKeyId, secretAccessKey)` |
| AWS   | IRSA              | `aws.WithIrsa(iamRoleArn)` |

Cloud-provider credential secret material (Azure SP client secret, AWS access-key pair) must be supplied
via `builder.AddParameter(..., secret: true)`. The integration never inlines
those credential values into Bicep or manifests; `rad credential register` resolves them during
deploy and redacts them from any logged command line. Secret Aspire parameters used in container
environment variables are emitted as `@secure()` Bicep parameters (never literals) and their values
are supplied to `rad deploy` separately, not by this credential-registration path.

> **Security note:** `rad credential register` accepts credential secrets only as command-line
> arguments, so during registration those resolved values are briefly visible to other users on the
> same host via the process table (`ps` / `/proc/<pid>/cmdline`). Log redaction does not mitigate
> this local, transient exposure. Deploy-time secret parameters do not share this concern — they are
> written to an owner-only temporary parameters file rather than the command line.

See the [Radius cloud providers documentation](https://docs.radapp.io/guides/deploy/environments/cloud-providers/)
for an end-to-end walkthrough.

## Reference

### Supported resources

* `AddContainer(...)` — published as a Radius container workload (`Radius.Compute/containers`).
* `AddProject<T>(...)` — published as a Radius container workload only when the project has a pre-built image attached with `WithContainerImage("<registry>/<image>:<tag>")`. Without one, `aspire publish` fails with a remediation message to build and push an image the cluster can pull.
* Selected resources with a Radius mapping (e.g. Redis, MongoDB, RabbitMQ, Dapr building blocks) emit Radius "legacy" types via the resource type mapper. Child database resources (for example `AddSqlServer("sql").AddDatabase("appdb")`) are collapsed onto the parent today.

Other Aspire resource types are not emitted; only the resources listed above appear in the generated Bicep.

### Diagnostics

The package uses the `ASPIRERADIUS` diagnostic prefix for two mechanisms: compile-time
`[Experimental]` gates on preview APIs, and runtime configuration/publish validation errors.

| Code | Mechanism | Surfaced as |
|------|-----------|-------------|
| `ASPIRERADIUS003` | Experimental gate on the cloud-provider surface (`WithAzureProvider` / `WithAwsProvider` and their credential callbacks) | `[Experimental]` warning (suppressible), documented at `https://aka.ms/aspire/diagnostics/<id>` |
| `ASPIRERADIUS004` | Experimental gate on the `ConfigureRadiusInfrastructure` escape hatch and its construct types | `[Experimental]` warning (suppressible) |
| `ASPIRERADIUS057` | Experimental gate on `WithContainerImage` | `[Experimental]` warning (suppressible) |

Runtime validation codes:

| Code | When | Meaning |
|------|------|---------|
| `ASPIRERADIUS010` | Provider config | A cloud-provider credential callback did not select a credential. |
| `ASPIRERADIUS011` | Provider config | Conflicting cloud-provider credentials across environments sharing a Radius installation. |
| `ASPIRERADIUS056` | Publish | Two emitted constructs map to the same Bicep identifier (e.g. a resource named `app` or `recipepack` colliding with a synthesized construct, or two resource names that sanitize to the same identifier such as `my-x` and `my.x`). Bicep symbols share one flat namespace; rename the conflicting resource. |

### Known limitations

* For `ASPIRERADIUS011`, AWS access-key credential conflicts are compared by the Aspire parameter name that supplies the access-key ID, not by the resolved access-key value. Two environments that use different parameter names for the same key can be flagged as a false conflict, while the same parameter name with different values is not flagged.
* Recipe customization, multiple Radius resource groups, secret stores, and cloud-managed resources are not part of this release; they are planned for follow-up releases.

## Additional documentation

* https://aspire.dev/integrations/gallery/
* https://docs.radapp.io/
* https://aspire.dev/

## Feedback & contributing

https://github.com/microsoft/aspire
