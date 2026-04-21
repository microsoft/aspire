# Kubernetes Ingress — Extensible Ingress Configuration for Aspire

## Problem Statement

When deploying Aspire applications to Kubernetes, resources with external HTTP endpoints need ingress configuration to be reachable from outside the cluster. Today, `Aspire.Hosting.Kubernetes` generates `Service` resources for each app model resource but does **not** generate any `Ingress` resources. Users must manually create ingress rules, install ingress controllers, and wire everything together — a significant gap in the "works out of the box" experience.

For Azure Kubernetes Service (AKS) specifically, the natural choice is **Azure Application Gateway for Containers** (AGC), the next-generation Layer 7 load balancer that supports both the Kubernetes Ingress API and the newer Gateway API. AGC replaces the older Application Gateway Ingress Controller (AGIC) and provides near-instant config sync, richer routing (header/query/method-based), canary releases, and mTLS.

However, the ingress mechanism is **inherently pluggable** — different providers (Nginx, Traefik, Ngrok, Tailscale, Cloudflare Tunnels, etc.) may want to supply their own ingress configuration. The design must leave room for third-party extensibility while providing a great default experience for AKS users.

## Goals

1. **Extensible ingress abstraction** in `Aspire.Hosting.Kubernetes` that any ingress provider can hook into
2. **Default AKS experience**: `AddAzureKubernetesEnvironment(...)` automatically provisions AGC and generates Ingress resources pointing external HTTP endpoints through the gateway
3. **Per-resource override**: Individual resources can opt out of or customize their ingress behavior
4. **Third-party friendly**: Third parties like Ngrok or Tailscale can implement their own ingress strategy without forking Aspire

## Current State

### What Exists

- **Ingress resource types** (`IngressV1`, `IngressSpecV1`, `IngressRuleV1`, etc.) exist in `Aspire.Hosting.Kubernetes/Resources/` but are **never generated** by the publisher
- **`KubernetesResource.GetTemplatedResources()`** yields `Workload`, `ConfigMap`, `Secret`, `Service`, persistent volumes, and `AdditionalResources` — no Ingress
- **`KubernetesServiceCustomizationAnnotation`** allows users to customize the `KubernetesResource` via `PublishAsKubernetesService(configure)` — this is the existing extensibility point
- **`KubernetesEnvironmentResource.DefaultServiceType`** controls the K8s Service type (defaults to `ClusterIP`)
- **External endpoints** are modeled via `EndpointAnnotation.IsExternal` but this information is not used to generate ingress rules
- The AKS spec (`aks-support.md`) lists ingress controller support as "🔲 Not Yet Implemented" under open questions

### Key Architecture

```
AddAzureKubernetesEnvironment("aks")
    └─ internally creates KubernetesEnvironmentResource
    └─ registers AzureKubernetesInfrastructure (eventing subscriber)
    └─ registers KubernetesInfrastructure (eventing subscriber)

KubernetesInfrastructure.OnBeforeStartAsync:
    for each compute resource:
        └─ CreateKubernetesResourceAsync → KubernetesResource
            └─ Workload (Deployment/StatefulSet)
            └─ Service (ClusterIP by default)
            └─ ConfigMap, Secret
        └─ DeploymentTargetAnnotation links resource → KubernetesResource

KubernetesPublishingContext.WriteKubernetesOutputAsync:
    for each resource with DeploymentTargetAnnotation:
        └─ apply KubernetesServiceCustomizationAnnotation callbacks
        └─ write Helm templates
```

## Design

### Core Principle: Delegate Ingress to the Environment

The ingress mechanism is a property of the **environment**, not of individual resources. A Kubernetes cluster might use Nginx ingress, an AKS cluster uses AGC, a Tailscale-enabled cluster uses Tailscale's ingress controller, etc.

Individual resources declare **intent** ("I have an external HTTP endpoint") and the **environment** decides **how** to fulfill that intent.

### Annotation-Based Extensibility

We introduce a callback-based annotation pattern (consistent with existing patterns like `KubernetesServiceCustomizationAnnotation`, `EnvironmentCallbackAnnotation`, etc.):

```csharp
/// <summary>
/// Configures ingress for a resource deployed to Kubernetes.
/// The callback receives the KubernetesResource and can add/modify
/// Ingress resources, annotations, or any other K8s objects needed.
/// </summary>
public sealed class KubernetesIngressConfigurationAnnotation(
    Func<KubernetesIngressContext, Task> configure) : IResourceAnnotation
{
    public Func<KubernetesIngressContext, Task> Configure { get; } = configure;
}
```

The `KubernetesIngressContext` provides:

```csharp
public sealed class KubernetesIngressContext
{
    /// <summary>
    /// The Kubernetes resource being configured (has access to
    /// EndpointMappings, Service, Workload, AdditionalResources, etc.)
    /// </summary>
    public required KubernetesResource KubernetesResource { get; init; }

    /// <summary>
    /// The original Aspire resource from the app model.
    /// </summary>
    public required IResource Resource { get; init; }

    /// <summary>
    /// The Kubernetes environment this resource is being deployed to.
    /// </summary>
    public required KubernetesEnvironmentResource Environment { get; init; }

    /// <summary>
    /// The external HTTP endpoint mappings that need ingress.
    /// Pre-filtered to only include external HTTP/HTTPS endpoints.
    /// </summary>
    public required IReadOnlyList<KubernetesResource.EndpointMapping> ExternalHttpEndpoints { get; init; }
}
```

### How It Works

#### Step 1: Environment sets the default ingress strategy

When `AddKubernetesEnvironment(...)` is called, it does **not** set any default ingress strategy — generic Kubernetes has no opinion on which ingress controller to use. Resources with external endpoints will get a `Service` but no `Ingress` unless the user configures one.

When `AddAzureKubernetesEnvironment(...)` is called, it adds a `KubernetesIngressConfigurationAnnotation` to the `KubernetesEnvironmentResource` that provisions AGC and generates standard Kubernetes `Ingress` resources with `ingressClassName: azure-alb`:

```csharp
// In AddAzureKubernetesEnvironment:
kubernetesEnvironment.Annotations.Add(
    new KubernetesIngressConfigurationAnnotation(ctx => 
    {
        // Generate Ingress resource with AGC ingress class
        // for each external HTTP endpoint
    }));
```

#### Step 2: Per-resource override (optional)

Users can override ingress for a specific resource:

```csharp
builder.AddProject<MyApi>()
    .WithExternalHttpEndpoint()
    .WithKubernetesIngress(ctx =>
    {
        // Custom ingress config for this specific resource
        // e.g., add specific annotations, custom host rules
    });
```

Or suppress ingress entirely:

```csharp
builder.AddProject<MyInternalApi>()
    .SuppressKubernetesIngress();
```

#### Step 3: Publisher resolves ingress at publish time

In `KubernetesInfrastructure.OnBeforeStartAsync`, after creating the `KubernetesResource`, the publisher:

1. Checks if the resource has external HTTP endpoints
2. If yes, looks for `KubernetesIngressConfigurationAnnotation` on the **resource** (via `TryGetLastAnnotation`)
3. If none found, looks for `KubernetesIngressConfigurationAnnotation` on the **environment**
4. If found, invokes the callback — which adds `Ingress` objects to `KubernetesResource.AdditionalResources`
5. If none found anywhere, no ingress is generated (backward compatible)

### API Surface: What Goes Where

The extension methods are carefully split between environment-level and resource-level targets:

#### Environment-level APIs (`IResourceBuilder<KubernetesEnvironmentResource>`)

These configure the **default ingress strategy** for all resources deployed to this environment:

```csharp
// Aspire.Hosting.Kubernetes — generic K8s environment
public static class KubernetesEnvironmentExtensions
{
    /// <summary>
    /// Sets the default ingress configuration for all resources with
    /// external HTTP endpoints deployed to this environment.
    /// Pass false to disable automatic ingress generation.
    /// </summary>
    public static IResourceBuilder<KubernetesEnvironmentResource> WithIngress(
        this IResourceBuilder<KubernetesEnvironmentResource> builder,
        Func<KubernetesIngressContext, Task> configure);

    /// <summary>
    /// Enables or disables automatic ingress generation for all resources
    /// in this environment.
    /// </summary>
    public static IResourceBuilder<KubernetesEnvironmentResource> WithIngress(
        this IResourceBuilder<KubernetesEnvironmentResource> builder,
        bool enabled);
}
```

```csharp
// Aspire.Hosting.Azure.Kubernetes — AKS-specific environment
public static class AzureKubernetesEnvironmentExtensions
{
    /// <summary>
    /// Configures the Azure Application Gateway for Containers instance
    /// that provides ingress for this AKS environment.
    /// Applied to the AKS environment resource, not individual resources.
    /// </summary>
    public static IResourceBuilder<AzureKubernetesEnvironmentResource> WithApplicationGateway(
        this IResourceBuilder<AzureKubernetesEnvironmentResource> builder,
        Action<AzureApplicationGatewayOptions>? configure = null);
}
```

#### Resource-level APIs (`IResourceBuilder<T> where T : IComputeResource`)

These configure ingress for a **specific resource**, overriding the environment default:

```csharp
// Aspire.Hosting.Kubernetes — per-resource overrides
public static class KubernetesServiceExtensions
{
    /// <summary>
    /// Overrides the ingress configuration for this specific resource,
    /// ignoring the environment-level default.
    /// </summary>
    public static IResourceBuilder<T> WithKubernetesIngress<T>(
        this IResourceBuilder<T> builder,
        Func<KubernetesIngressContext, Task> configure) where T : IComputeResource;
}
```

> **Note:** There is no `SuppressKubernetesIngress()` method. External endpoints are already
> opt-in via `WithExternalHttpEndpoint()` / `e.IsExternal = true`. If a resource doesn't have
> external endpoints, no ingress is generated for it. To prevent ingress for a resource that
> does have external endpoints, simply don't mark those endpoints as external.

#### Summary Table

| Method | Target | Purpose |
|--------|--------|---------|
| `WithIngress(configure)` | `KubernetesEnvironmentResource` | Set default ingress strategy for the environment |
| `WithIngress(false)` | `KubernetesEnvironmentResource` | Disable default ingress for all resources |
| `WithApplicationGateway(configure?)` | `AzureKubernetesEnvironmentResource` | Configure AGC (AKS-specific) |
| `WithKubernetesIngress(configure)` | `IComputeResource` | Override ingress for one resource |

### Third-Party Extensibility

Third parties provide their own ingress strategy via the **environment-level** `WithIngress` API. For example:

**Ngrok:**
```csharp
// Hypothetical Aspire.Hosting.Ngrok package
public static IResourceBuilder<KubernetesEnvironmentResource> WithNgrokIngress(
    this IResourceBuilder<KubernetesEnvironmentResource> builder,
    string authToken)
{
    return builder.WithIngress(ctx =>
    {
        // Add ngrok-specific Ingress with ingressClassName: "ngrok"
        // and k8s.ngrok.com/modules annotations
        return Task.CompletedTask;
    });
}
```

**Tailscale:**
```csharp
// Hypothetical Aspire.Hosting.Tailscale package
public static IResourceBuilder<KubernetesEnvironmentResource> WithTailscaleIngress(
    this IResourceBuilder<KubernetesEnvironmentResource> builder)
{
    return builder.WithIngress(ctx =>
    {
        // Set Service type to LoadBalancer with Tailscale annotations
        // tailscale.com/expose: "true"
        return Task.CompletedTask;
    });
}
```

Because the annotation uses `TryGetLastAnnotation` (last wins), a third party can simply add their annotation after the environment is created and it will override any default:

```csharp
var aks = builder.AddAzureKubernetesEnvironment("aks");
// AGC is the default, but override with Ngrok:
aks.WithNgrokIngress(authToken);
```

### AKS + Application Gateway for Containers — Default Implementation

When `AddAzureKubernetesEnvironment(...)` is called:

1. **Azure provisioning** (Bicep via `ConfigureAksInfrastructure`):
   - Provision an `ApplicationGatewayForContainers` resource
   - Create a dedicated subnet for AGC (or use user-provided subnet)
   - Install the ALB Controller add-on on the AKS cluster
   - Create the necessary role assignments (reader on resource group, contributor on AGC)

2. **Kubernetes manifests** (via the ingress annotation set on the inner `KubernetesEnvironmentResource`):
   - For each resource with external HTTP endpoints, generate an `Ingress` resource:
     ```yaml
     apiVersion: networking.k8s.io/v1
     kind: Ingress
     metadata:
       name: <resource-name>-ingress
       annotations:
         alb.networking.azure.io/alb-id: <agc-resource-id>
     spec:
       ingressClassName: azure-alb-external
       rules:
         - http:
             paths:
               - path: /
                 pathType: Prefix
                 backend:
                   service:
                     name: <service-name>
                     port:
                       number: <port>
     ```
   - The `Ingress` is added to `KubernetesResource.AdditionalResources` so it's included in the Helm chart

3. **End-to-end AKS user experience**:
   ```csharp
   // Simplest case: AGC provisioned automatically, Ingress generated
   // for any resource with external HTTP endpoints
   var aks = builder.AddAzureKubernetesEnvironment("aks");

   builder.AddProject<MyApi>()
       .WithExternalHttpEndpoint();
   // → AGC provisioned in Bicep
   // → Ingress resource generated pointing to MyApi's service

   // Customize AGC provisioning:
   var aks = builder.AddAzureKubernetesEnvironment("aks")
       .WithApplicationGateway(gw =>
       {
           gw.SkuTier = ApplicationGatewayTier.Standard;
       });

   // Override ingress for a specific resource:
   builder.AddProject<MySpecialApi>()
       .WithExternalHttpEndpoint()
       .WithKubernetesIngress(ctx =>
       {
           // Custom Ingress with specific host rules, TLS config, etc.
       });

   // Disable ingress entirely for this environment:
   var k8s = builder.AddKubernetesEnvironment("k8s")
       .WithIngress(false);
   ```

### Domain Names and AGC

Azure Application Gateway for Containers has a specific model for domain names:

- **Auto-generated FQDN**: When an Ingress is created without a `host` field, AGC auto-assigns an FQDN in the form `<frontend-name>.<hash>.<region>.appgw.containers.azure.net`. This means resources get publicly accessible URLs immediately without any DNS configuration.

- **Custom domains**: Users can specify a custom `host` in their Ingress rules and configure DNS (CNAME or A record) to point to the AGC frontend IP. This is handled outside of Aspire's scope initially.

- **Domain assignment is per-frontend, not per-gateway**: AGC has a concept of `Frontend` sub-resources associated with the traffic controller. Each frontend gets its own FQDN. Multiple Ingress resources sharing the same `ingressClassName` share the same frontend.

- **For Aspire's default implementation**: We generate Ingress resources **without** a `host` field, so AGC auto-assigns FQDNs. The auto-generated FQDN can be discovered post-deployment and surfaced in the deployment summary. Future work could expose this as a resource URL/connection string.

### TLS Certificates and Custom Domains

AGC supports three TLS scenarios, each with a different certificate model:

1. **Auto-generated FQDN (default for Aspire)**
   - When no `host` is specified in the Ingress, AGC assigns an FQDN under `*.appgw.containers.azure.net`
   - Microsoft automatically provisions and manages a TLS certificate for this FQDN
   - **No user action required** — HTTPS works out of the box with the auto-assigned domain
   - This is the default Aspire experience: zero-config TLS

2. **Custom domain with Kubernetes TLS Secret**
   - User provides their own certificate as a Kubernetes `kubernetes.io/tls` Secret
   - The Ingress references the secret via the `tls` spec:
     ```yaml
     spec:
       tls:
         - hosts:
             - app.example.com
           secretName: my-tls-secret
       rules:
         - host: app.example.com
           http:
             paths: [...]
     ```
   - User manages certificate lifecycle (provisioning, renewal, rotation)
   - This is configurable per-resource via `WithKubernetesIngress()` today

3. **Custom domain with Azure Key Vault integration**
   - Certificate is stored in Azure Key Vault and referenced by AGC
   - AGC **polls Key Vault** (approximately every 4 hours) and picks up new certificate versions automatically — this is not ACME; AGC is a certificate **consumer**, not an issuer. If you need automatic renewal (e.g., via Let's Encrypt/ACME), that must be configured separately to update the certificate in Key Vault.
   - The gateway needs a managed identity with Key Vault `Secrets/Get` permission
   - This is the recommended production approach for custom domains
   - Could be exposed in future as `.WithCustomDomain("app.example.com", keyVaultCertificate)`

4. **Future: cert-manager with Key Vault extension** (not in first pass)
   - [cert-manager](https://cert-manager.io/) can be installed in the cluster to automate certificate issuance and renewal via ACME (Let's Encrypt) or other issuers
   - The [Azure Key Vault provider for cert-manager](https://github.com/cert-manager/csi-driver) can sync issued certificates into Key Vault, which AGC then picks up automatically
   - This provides a fully automated end-to-end flow: cert-manager issues/renews → Key Vault stores → AGC polls and uses
   - Could be exposed as `.WithCertManager()` on the AKS environment in a future phase

**For the initial implementation**, we target scenario 1 (auto-generated FQDN with managed cert). Custom domain support (scenarios 2 and 3) can be layered on later via the `WithKubernetesIngress()` per-resource override or a future `WithCustomDomain()` API.

### Azure Infrastructure for AGC

AGC uses the `Microsoft.ServiceNetworking/trafficControllers` Azure resource type (not the traditional `Microsoft.Network/applicationGateways`). The Bicep provisioning includes:

```bicep
// Traffic Controller (Application Gateway for Containers)
resource trafficController 'Microsoft.ServiceNetworking/trafficControllers@2023-11-01' = {
  name: trafficControllerName
  location: location
  properties: {}
}

// Frontend association  
resource frontend 'Microsoft.ServiceNetworking/trafficControllers/frontends@2023-11-01' = {
  parent: trafficController
  name: 'default'
  location: location
  properties: {}
}

// Association with AKS subnet
resource association 'Microsoft.ServiceNetworking/trafficControllers/associations@2023-11-01' = {
  parent: trafficController
  name: 'default'
  location: location
  properties: {
    associationType: 'subnets'
    subnet: {
      id: agcSubnetId
    }
  }
}
```

The ALB Controller is installed as an AKS add-on (`appGwIngress` or via Helm chart `oci://mcr.microsoft.com/application-lb/charts/alb-controller`). The controller watches for Ingress resources with `ingressClassName: azure-alb-external` and configures AGC accordingly.

## Annotation Lookup Strategy

The resolution order for ingress configuration:

1. **Resource-level** `KubernetesIngressConfigurationAnnotation` (highest priority — per-resource override)
2. **Environment-level** `KubernetesIngressConfigurationAnnotation` (default for all resources in this environment)
3. **No annotation** → no ingress generated (backward compatible)

This follows the existing pattern where `TryGetLastAnnotation<T>` returns the most recently added annotation, so adding an annotation after the environment is configured effectively overrides it.

## Detecting External HTTP Endpoints

An endpoint is considered "external HTTP" if:

- `EndpointAnnotation.IsExternal == true` AND
- `EndpointAnnotation.UriScheme` is `"http"` or `"https"` (or the protocol is `TCP` with an HTTP scheme)

This maps to the existing endpoint model — no new attributes needed. Resources that want external ingress use `.WithExternalHttpEndpoint()` or `.WithEndpoint(..., e => e.IsExternal = true)`.

## Open Questions

1. **Gateway API vs Ingress API**: AGC supports both the legacy Kubernetes Ingress API and the newer Gateway API (`gateway.networking.k8s.io/v1`). Should we generate Gateway API resources instead of / in addition to Ingress? The Gateway API is the future direction and offers richer routing capabilities, but Ingress is more universally supported.

2. **Host-based routing**: Should we auto-generate hostnames for ingress rules? For AGC, Azure auto-assigns `*.<hash>.appgw.containers.azure.net` hostnames when no host is specified. We could expose this as a connection string / URL output after deployment. How should this interact with custom domains in the future?

3. **Multiple external endpoints per resource**: If a resource has multiple external HTTP endpoints, should we generate one Ingress with multiple rules, or one Ingress per endpoint?

4. **Non-HTTP external endpoints**: Some resources may have external TCP endpoints (e.g., databases). These typically need `Service` type `LoadBalancer` rather than `Ingress`. Should the ingress annotation also handle this case, or is that a separate concern?

## Implementation Phases

### Phase 1: Core Extensibility (Aspire.Hosting.Kubernetes)
- Add `KubernetesIngressConfigurationAnnotation` and `KubernetesIngressContext`
- Add ingress resolution logic to the publishing pipeline
- Add `WithKubernetesIngress()` and `SuppressKubernetesIngress()` extension methods
- No default behavior change — existing deployments unaffected

### Phase 2: AKS Default Ingress (Aspire.Hosting.Azure.Kubernetes)
- Provision AGC in `ConfigureAksInfrastructure`
- Add default `KubernetesIngressConfigurationAnnotation` to AKS environment
- Generate Ingress resources for resources with external HTTP endpoints
- Add `WithApplicationGateway()` and `WithoutDefaultIngress()` extensions

### Phase 3: Polish
- Expose AGC-assigned URLs as resource connection strings
- Support custom domains and TLS configuration
- Documentation and samples
