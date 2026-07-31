// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Radius.Publishing;

/// <summary>
/// Single source of truth for how Aspire addresses a Radius container across the cluster, so the
/// service-discovery values emitted into consumer containers (the <c>services__*</c> env vars) can
/// never disagree with the Kubernetes objects the Radius recipe actually creates.
/// </summary>
/// <remarks>
/// The Radius Kubernetes container recipe (radius-project/resource-types-contrib) creates one
/// ClusterIP <c>Service</c> per container that declares ports. The Service name is
/// <c>${normalizedName}-${containerName}</c> and it is exposed on the container port
/// (<c>port == targetPort == containerPort</c>). Pinned to an immutable commit so the documented
/// contract can be verified even if <c>main</c> moves (Service name at line 338, port/targetPort at
/// lines 329-330):
/// <see href="https://github.com/radius-project/resource-types-contrib/blob/ab9722ac7027060693ef6d731f770436b03abbaf/Compute/containers/recipes/kubernetes/bicep/kubernetes-containers.bicep"/>.
/// <para>
/// For a Radius container emitted by Aspire, both <c>normalizedName</c> (the top-level <c>name:</c>)
/// and <c>containerName</c> (the <c>properties.containers</c> map key) equal the Aspire resource
/// name, so the Service name is <c>{resource.Name}-{resource.Name}</c>. The Radius recipe and the
/// container v2 schema do <em>not</em> require the map key to equal <c>name:</c> — Radius permits
/// distinct top-level and container-map names. Aspire requires them to match because it derives
/// service discovery from the original resource name, so a callback that renames only one of them
/// would make the emitted <c>services__*</c> values address a Service that is never produced; the
/// publisher guards against that so this doubling holds for every manifest Aspire emits.
/// </para>
/// </remarks>
internal static class RadiusServiceDiscovery
{
    // The Kubernetes container port assigned to a project resource whose HTTP endpoint has no
    // explicit target port in publish mode. Mirrors the Kubernetes publisher's default
    // (GenerateDefaultProjectEndpointMapping) so a project turned into a Radius container declares
    // a port -> the recipe creates a Service for it -> it is reachable via service discovery.
    internal const int DefaultProjectContainerPort = 8080;

    /// <summary>
    /// Gets the Kubernetes <c>Service</c> name the Radius recipe creates for <paramref name="resource"/>.
    /// </summary>
    public static string GetServiceName(IResource resource) => GetServiceName(resource.Name);

    /// <summary>
    /// Gets the Kubernetes <c>Service</c> name the Radius recipe creates for a container whose
    /// Aspire resource name is <paramref name="resourceName"/>. Aspire keys both the top-level
    /// <c>name:</c> and the <c>properties.containers</c> map key by the resource name, so the
    /// recipe's <c>${normalizedName}-${containerName}</c> resolves to <c>{name}-{name}</c>.
    /// </summary>
    public static string GetServiceName(string resourceName) => GetServiceName(resourceName, resourceName);

    /// <summary>
    /// Gets the Kubernetes <c>Service</c> name the Radius recipe creates for a container whose
    /// top-level <c>name:</c> is <paramref name="topLevelName"/> and whose
    /// <c>properties.containers</c> map key is <paramref name="mapKey"/>. The recipe names the
    /// Service <c>${normalizedName}-${containerName}</c>, i.e. <c>{topLevelName}-{mapKey}</c>. Use
    /// this overload for callback-mutated containers whose top-level name is allowed to differ from
    /// the map key (no service-discovery contract exists for them).
    /// </summary>
    public static string GetServiceName(string topLevelName, string mapKey) => $"{topLevelName}-{mapKey}";

    /// <summary>
    /// Resolves the port the Radius recipe's <c>Service</c> exposes for the endpoint named
    /// <paramref name="endpointName"/> on <paramref name="resource"/> (equivalently, the container
    /// port emitted into the Bicep). Returns <see langword="null"/> when no port should be emitted
    /// for this endpoint (so no Service is created for it).
    /// </summary>
    /// <remarks>
    /// Port resolution runs through <see cref="ResourceExtensions.ResolveEndpoints"/> — the same
    /// primitive the Kubernetes publisher uses — so the container/Service port is computed with the
    /// framework's resource-aware semantics rather than a hand-rolled <c>TargetPort ?? Port</c>:
    /// <list type="bullet">
    /// <item>an explicit target port is used as-is;</item>
    /// <item>a <see cref="ContainerResource"/> with only a host port listens on that port;</item>
    /// <item>an endpoint without an explicit port that <see cref="ResourceExtensions.ResolveEndpoints"/>
    /// assigns a distinct allocated port to uses that allocated port, so multiple portless endpoints
    /// never collapse onto the same Service port;</item>
    /// <item>a <see cref="ProjectResource"/> endpoint that resolves to no port — the first portless
    /// HTTP or HTTPS endpoint for its scheme (the deployment tool would normally assign one) — is
    /// defaulted to <see cref="DefaultProjectContainerPort"/>, <em>except</em> the synthetic default
    /// HTTPS endpoint, which returns <see langword="null"/> (see the port-resolution code below).</item>
    /// </list>
    /// A resolution is stateless and deterministic (the allocator always starts from the same port
    /// and endpoints are enumerated in a stable order), so the two independent callers — the Bicep
    /// container-port emission and the <c>services__*</c> URL emission — always agree.
    /// </remarks>
    public static int? ResolveServicePort(IResource resource, string endpointName)
    {
        var resolved = resource.ResolveEndpoints()
            .FirstOrDefault(r => string.Equals(r.Endpoint.Name, endpointName, StringComparison.OrdinalIgnoreCase));

        if (resolved is null)
        {
            return null;
        }

        // ResolveEndpoints computes the container (target) port with resource-aware rules. When it
        // yields a concrete port (explicit target, a container's host-derived port, or an allocated
        // port for an otherwise-portless endpoint), that is the container/Service port.
        if (resolved.TargetPort.Value is int targetPort)
        {
            return targetPort;
        }

        // No resolved target port: ResolveEndpoints returns None only for a project's default
        // endpoint for its scheme (the first portless http/https endpoint), which the deployment
        // tool would normally assign a port to.
        //
        // Skip only the *synthetic* default HTTPS endpoint: containers do not terminate TLS
        // in-cluster, and the framework reuses the HTTP port for it (see the Kubernetes publisher's
        // DefaultHttpsEndpoint handling and the core SetBothPortsEnvVariables behavior). Any other
        // portless project endpoint — the default HTTP endpoint or an explicit portless HTTP/HTTPS
        // endpoint — is given the standard container port so the container declares a port and the
        // recipe creates a Service, matching the Kubernetes publisher's 8080 default.
        // See: https://github.com/microsoft/aspire/issues/14029
        if (resource is IProjectLaunchDefaultsResource projectResource &&
            ReferenceEquals(resolved.Endpoint, projectResource.DefaultHttpsEndpoint))
        {
            return null;
        }

        return DefaultProjectContainerPort;
    }

    public static string ToInvariantString(int value) => value.ToString(CultureInfo.InvariantCulture);
}
