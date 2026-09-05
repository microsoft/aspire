# Deployment stamps

This document describes how Aspire models a compute resource that is deployed to more than one compute
environment, and how Azure Front Door acts as the global entry point in front of those deployments.

## Motivation

The Azure [deployment stamp pattern](https://learn.microsoft.com/azure/architecture/patterns/deployment-stamp)
deploys identical copies of a workload to several regions and places a global routing layer in front of them.
Before this feature Aspire could not express that topology: a compute resource was bound to exactly one
compute environment, and Azure Front Door emitted a dedicated endpoint, origin group, origin, and route per
backend, so each backend had its own hostname and there was nothing to fail over between.

## Model

A **stamp** is the pairing of a compute resource with one of the compute environments it is deployed to,
represented by `Aspire.Hosting.ApplicationModel.ComputeStamp`:

```csharp
public sealed class ComputeStamp
{
    public IComputeEnvironmentResource Environment { get; }
    public string Name { get; }
    public bool QualifiesNames { get; }
}
```

Binding is expressed with these APIs on any `IComputeResource`:

| API | Behaviour |
|---|---|
| `WithComputeEnvironment(env)` | Binds to a single environment. Unchanged. |
| `WithComputeEnvironments(env1, env2, …)` | Binds to several environments. The stamp name defaults to the environment's resource name. |
| `WithStamp(env, stampName)` | Binds one stamp with an explicit short name. Repeatable. |

Inspection helpers:

| API | Returns |
|---|---|
| `GetComputeStamps()` | Every stamp, in declaration order. |
| `GetComputeEnvironments()` | Every bound compute environment. |
| `IsBoundToComputeEnvironment(env)` | Whether the resource is bound to a specific environment. |
| `IsStamped()` | Whether the resource is bound to more than one environment. |
| `GetStampQualifiedName(env)` | The infrastructure name to use for the resource in that environment. |
| `GetDeploymentTargetAnnotations()` | Every deployment target, one per stamp. Never throws. |

### Name stability

`GetStampQualifiedName` returns the plain resource name whenever the resource is bound to a single compute
environment and no explicit stamp name was supplied (`ComputeStamp.QualifiesNames` is `false`). This is a
load-bearing invariant: every generated container app name, web site name, Bicep module path, Bicep
parameter, and pipeline step name for a single-region application is byte-for-byte what earlier versions
produced, so redeploying an existing application does not recreate its infrastructure.

Adding a second compute environment to an existing resource *does* change its generated names, because the
stamps must be distinguishable. Azure Container Apps caps app names at 32 characters, so use
`WithStamp(env, "eus")` with short stamp names when the environment names are long.

Every place that derives a name from a compute resource goes through `GetStampQualifiedName`:

- `ContainerAppEnvironmentContext.CreateContainerAppAsync` — the `-containerapp` resource name
- `BaseContainerAppContext.NormalizedContainerAppName` — the container app name and endpoint map
- `AzureContainerAppEnvironmentResource.GetHostAddressExpression` — the regional hostname
- `AzureAppServiceEnvironmentContext.CreateAppServiceAsync` — the `-website` resource name
- `AzureAppServiceWebSiteResource.GetAppServiceWebsiteBaseNameAsync` and
  `AzureAppServiceEnvironmentResource.GetHostAddressExpression` — the web site name and hostname
- `AzurePublishingContext` — the Bicep module directory and file for each deployment target
- `AzureContainerAppResource` / `AzureAppServiceWebSiteResource` — `deploy-{name}` and
  `print-{name}-summary` pipeline step names

## Regions

`WithLocation` sets the Azure region of an individual Azure resource:

```csharp
var eastus = builder.AddAzureContainerAppEnvironment("aca-eastus").WithLocation("eastus");
var westeu = builder.AddAzureContainerAppEnvironment("aca-westeu").WithLocation("westeurope");
```

It writes `AzureBicepResource.KnownParameters.Location` and records an `AzureResourceLocationAnnotation`. The
annotation is what distinguishes an author's explicit choice from the location the provisioner infers from
the Azure environment, because both end up in the same parameter slot.

- **Deploy path**: already honoured. `BicepProvisioner.PopulateWellKnownParameters` only fills in the
  environment location when the resource does not already carry one, and `GetEffectiveLocation` reads the
  per-resource value.
- **Publish path**: `AzurePublishingContext` now passes the resource's own location to the module when one is
  configured, and skips the `location` entry in the parameter loop. Previously it added `location`
  unconditionally and then re-added it from `resource.Parameters`, which threw on a duplicate key as soon as
  a resource carried its own location.

All stamps deploy into the application's single resource group. Azure allows a resource group to contain
resources from any region, so multiple regions do not require multiple resource groups. Per-stamp resource
groups are a possible follow-up: `AzureBicepResource.Scope` already supports per-module resource group
scoping, but `main.bicep` emits exactly one `ResourceGroup` and `BicepProvisioner` resolves rather than
creates scoped resource groups.

## Deployment targets

`DeploymentTargetAnnotation` already carried a `ComputeEnvironment`, so a stamped resource simply has one
annotation per stamp. Each compute environment's `PrepareDeploymentTargetsAsync` now tests membership
(`IsBoundToComputeEnvironment`) rather than equality, so a resource bound to several environments is
processed by all of them.

`GetDeploymentTargetAnnotation()` still throws when a resource has several deployment targets and no
environment was supplied — that remains a useful guard against accidentally leaving a resource unbound in a
model with two environments. For a resource that is intentionally stamped the message instead points at
`GetDeploymentTargetAnnotations()`. Callers that must handle every stamp use the plural accessor.

## Container registries

A stamped resource's image is built and pushed **once**, and every stamp pulls that same image. All of a
stamped resource's compute environments must therefore share one container registry. Each
`AddAzureContainerAppEnvironment` provisions its own registry by default, so a stamped application has to
point its environments at a shared one:

```csharp
var acr = builder.AddAzureContainerRegistry("acr");

var eastus = builder.AddAzureContainerAppEnvironment("aca-eastus").WithLocation("eastus").WithContainerRegistry(acr);
var westeu = builder.AddAzureContainerAppEnvironment("aca-westeu").WithLocation("westeurope").WithContainerRegistry(acr);
```

`ResourceExtensions.GetContainerRegistry` fails with an actionable message when a stamped resource's stamps
resolve to different registries. Without that check every stamp after the first would be emitted with an
image reference into a registry the image was never pushed to, and whose managed identity has no pull
rights — a guaranteed image-pull failure at deployment time rather than an error in the app model.

Pushing one image to several registries is a possible follow-up; it would require resolving the image
reference per deployment target rather than once per resource.

## References between stamped resources

References resolve **within the same stamp**. When `web` in `eastus` references `api`, and `api` is stamped to
both `eastus` and `westeurope`, `web` gets the `eastus` copy of `api`, so traffic never leaves the region to
reach a dependency that exists locally.

`ComputeEnvironmentEndpointResolver.TryGetCrossEnvironmentEndpointExpression` returns `false` — deferring to
the publisher's local endpoint map, which is already per-environment — as soon as *any* of the target's
environments is the environment currently being generated. `TryGetEffectiveComputeEnvironment` throws for a
resource stamped across several environments, because there is no single environment to resolve; the message
directs the caller to reference the resource through a global entry point instead.

## Azure Front Door as the global entry point

`WithOrigin(resource)` still produces one Front Door endpoint, origin group, and route per call, so each
backend keeps its own `*.azurefd.net` hostname. What changed is that the origin group now contains **one
origin per stamp**, each pointing at that stamp's regional hostname via its own compute environment's
`GetHostAddressExpression`:

```
CdnProfile (Global)
└── apiEndpoint                    ← one global hostname
    ├── apiOriginGroup             ← health probe + load balancing
    │   ├── api_aca_eastusOrigin   ← stamp 1
    │   └── api_aca_westeuOrigin   ← stamp 2
    └── apiRoute (dependsOn: every origin)
output api_endpointUrl
```

`WithOriginGroup(resource, configure)` exposes the routing controls:

- `WithRouting(FrontDoorOriginRouting)` — `LatencyBased` (default), `Failover`, `Weighted`
- `WithStampPriority(env, priority)` / `WithStampWeight(env, weight)`
- `WithHealthProbe(path, protocol, interval)`
- `WithLoadBalancing(sampleSize, successfulSamplesRequired, additionalLatencyMilliseconds)`
- `WithSessionAffinity(enabled)` / `WithTrafficRestorationTime(timeSpan)`
- `WithCustomDomain(hostName)`

`priority` and `weight` are only emitted when they carry information — an explicit per-stamp value, or
`Failover` routing, which assigns ascending priorities in declaration order. That keeps the generated Bicep
for a default single-stamp application identical to what earlier versions produced.

Front Door itself is a global resource: it is deployed once and routes to the regional stamps of its origins.
Binding an `AzureFrontDoorResource` to compute environments is rejected.

### Custom domains and the module cycle

An application cannot be told its own public Front Door hostname through the generated `*.azurefd.net` name.
Front Door depends on the application's host address, so the reverse dependency would create a Bicep module
cycle. A custom domain hostname is supplied by the author and therefore known before deployment, which makes
`WithCustomDomain` the way to give an application its public address. The DNS TXT validation token needed to
prove domain ownership is emitted as the `{origin}_customDomainValidationToken` output.

For the same reason, locking origins down to Front Door traffic via the `X-Azure-FDID` header is not modelled:
the Front Door ID is generated by the Front Door module, so injecting it into the origins would close the same
cycle. It remains a manual post-deployment step.

## Out of scope

- **Per-stamp backing services.** Databases, caches, and storage are not replicated per stamp; every stamp
  references the same instance. `ComputeStamp` carries the environment, so a future per-stamp backing service
  feature can key off the same annotation.
- **Per-stamp resource groups.** See "Regions" above.
- **Kubernetes, Docker Compose, and Radius stamping.** The core model is publisher-agnostic, but only Azure
  Container Apps and Azure App Service have stamp-aware name derivation. Kubernetes derives service names
  from `resource.Name`, which would collide across stamps.
- **Manifest publishing.** The manifest schema models a single deployment target per resource, so a stamped
  resource is represented by its first stamp. Use the Azure publisher to emit infrastructure for every stamp.
