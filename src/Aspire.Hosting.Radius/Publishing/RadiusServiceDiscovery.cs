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
/// (<c>port == targetPort == containerPort</c>):
/// <see href="https://github.com/radius-project/resource-types-contrib/blob/main/Compute/containers/recipes/kubernetes/bicep/kubernetes-containers.bicep"/>.
/// <para>
/// For a Radius container emitted by Aspire, both <c>normalizedName</c> (the top-level <c>name:</c>)
/// and <c>containerName</c> (the <c>properties.containers</c> map key) equal the Aspire resource
/// name, so the Service name is <c>{resource.Name}-{resource.Name}</c>. Both the recipe and the
/// container v2 schema require the map key to equal <c>name:</c>, so a callback that renames only
/// one of them produces an invalid manifest; the publisher guards against that so this doubling
/// holds for every valid manifest.
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
    public static string GetServiceName(IResource resource) => $"{resource.Name}-{resource.Name}";

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
    /// <item>a <see cref="ProjectResource"/>'s default HTTP endpoint has no resolved port (the
    /// deployment tool would assign one), so it is defaulted to <see cref="DefaultProjectContainerPort"/>;</item>
    /// <item>any other endpoint without an explicit port is given a distinct allocated port, so
    /// multiple portless endpoints never collapse onto the same Service port.</item>
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
