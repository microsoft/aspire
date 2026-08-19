// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;
using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting;

/// <summary>
/// Helper for resolving endpoint references that point at a resource deployed to a different
/// compute environment than the one currently generating deployment artifacts.
/// </summary>
/// <remarks>
/// Compute environment publishers (App Service, Azure Container Apps, Kubernetes) resolve
/// endpoint references against their own local endpoint map. When a resource references an
/// endpoint owned by a resource deployed to a different compute environment (for example a
/// Foundry hosted agent deployed to an <c>AzureCognitiveServicesProjectResource</c>), the
/// endpoint is not present in the local map and the lookup fails. In that case the owning
/// compute environment knows how to express the endpoint property, so we delegate to it.
/// This mirrors the inverse-direction logic used by Foundry hosted agents when they reference
/// endpoints owned by App Service/ACA/Kubernetes resources.
/// </remarks>
internal static class ComputeEnvironmentEndpointResolver
{
    /// <summary>
    /// Attempts to produce a <see cref="ReferenceExpression"/> for an endpoint's URL by
    /// delegating to the compute environment that owns the endpoint's resource, when that
    /// environment is different from the publisher's current compute environment(s).
    /// </summary>
    /// <param name="endpointReference">The endpoint reference to resolve.</param>
    /// <param name="currentComputeEnvironments">
    /// The compute environment(s) the current publisher is generating artifacts for. When the
    /// endpoint's owning resource deploys to one of these, resolution is left to the local
    /// endpoint map and this method returns <see langword="false"/>.
    /// </param>
    /// <param name="expression">
    /// When this method returns <see langword="true"/>, contains the delegated reference expression.
    /// </param>
    /// <returns>
    /// <see langword="true"/> if the endpoint is owned by a different compute environment and a
    /// delegated expression was produced; otherwise <see langword="false"/>.
    /// </returns>
    public static bool TryGetCrossEnvironmentEndpointExpression(
        EndpointReference endpointReference,
        IReadOnlyList<IComputeEnvironmentResource?> currentComputeEnvironments,
        [NotNullWhen(true)] out ReferenceExpression? expression)
    {
        ArgumentNullException.ThrowIfNull(endpointReference);

        return TryGetCrossEnvironmentEndpointExpression(
            endpointReference.Property(EndpointProperty.Url),
            currentComputeEnvironments,
            out expression);
    }

    /// <summary>
    /// Attempts to produce a <see cref="ReferenceExpression"/> for an endpoint property by
    /// delegating to the compute environment that owns the endpoint's resource, when that
    /// environment is different from the publisher's current compute environment(s).
    /// </summary>
    /// <param name="endpointReferenceExpression">The endpoint reference expression to resolve.</param>
    /// <param name="currentComputeEnvironments">
    /// The compute environment(s) the current publisher is generating artifacts for. When the
    /// endpoint's owning resource deploys to one of these, resolution is left to the local
    /// endpoint map and this method returns <see langword="false"/>.
    /// </param>
    /// <param name="expression">
    /// When this method returns <see langword="true"/>, contains the delegated reference expression.
    /// </param>
    /// <returns>
    /// <see langword="true"/> if the endpoint is owned by a different compute environment and a
    /// delegated expression was produced; otherwise <see langword="false"/>.
    /// </returns>
    public static bool TryGetCrossEnvironmentEndpointExpression(
        EndpointReferenceExpression endpointReferenceExpression,
        IReadOnlyList<IComputeEnvironmentResource?> currentComputeEnvironments,
        [NotNullWhen(true)] out ReferenceExpression? expression)
    {
        ArgumentNullException.ThrowIfNull(endpointReferenceExpression);
        ArgumentNullException.ThrowIfNull(currentComputeEnvironments);

        expression = null;

        var owningResource = endpointReferenceExpression.Endpoint.Resource;

        // A stamped resource is deployed to several compute environments. When one of them is the
        // environment currently being generated, the reference must stay inside that stamp so traffic
        // never crosses a region boundary to reach a dependency that exists locally. The local endpoint
        // map already resolves to the local stamp, so leave it alone.
        foreach (var owned in owningResource.GetComputeEnvironments())
        {
            foreach (var current in currentComputeEnvironments)
            {
                if (ReferenceEquals(current, owned))
                {
                    return false;
                }
            }
        }

        // Resolve the compute environment the owning resource deploys to. A plain resource that is
        // not deployed anywhere has none, so there is nothing to delegate to and the local lookup
        // handles it.
        if (!TryGetEffectiveComputeEnvironment(owningResource, out var owningComputeEnvironment))
        {
            return false;
        }

        // If the owning resource deploys to one of the current publisher's compute environments, the
        // endpoint lives in the local endpoint map. Leave resolution to the existing local lookup so
        // generated artifacts (bicep parameters, helm values, etc.) are unchanged.
        foreach (var current in currentComputeEnvironments)
        {
            if (ReferenceEquals(current, owningComputeEnvironment))
            {
                return false;
            }
        }

#pragma warning disable ASPIRECOMPUTE002 // Experimental: compute environment endpoint expression
        expression = owningComputeEnvironment.GetEndpointPropertyExpression(endpointReferenceExpression);
#pragma warning restore ASPIRECOMPUTE002

        return true;
    }

    /// <summary>
    /// Resolves the compute environment that a resource is deployed to. A resource may be bound to a
    /// compute environment explicitly (via <see cref="ResourceExtensions.GetComputeEnvironment"/>) or
    /// implicitly through its deployment target.
    /// </summary>
    /// <param name="resource">The resource whose compute environment should be resolved.</param>
    /// <param name="computeEnvironment">
    /// When this method returns <see langword="true"/>, contains the owning compute environment.
    /// </param>
    /// <returns>
    /// <see langword="true"/> if a compute environment was resolved; otherwise <see langword="false"/>.
    /// </returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the resource is deployed as several regional stamps, because there is no single
    /// environment to resolve. Callers that can handle stamps should use
    /// <see cref="ResourceExtensions.GetComputeStamps"/> instead.
    /// </exception>
    public static bool TryGetEffectiveComputeEnvironment(
        IResource resource,
        [NotNullWhen(true)] out IComputeEnvironmentResource? computeEnvironment)
    {
        ArgumentNullException.ThrowIfNull(resource);

        var boundComputeEnvironments = resource.GetComputeEnvironments();
        if (boundComputeEnvironments.Count > 1)
        {
            var names = string.Join("', '", boundComputeEnvironments.Select(e => e.Name));
            throw new InvalidOperationException(
                $"Resource '{resource.Name}' is deployed as multiple stamps across compute environments '{names}', so a single endpoint cannot be resolved. " +
                "Reference the resource from within one of those environments, or route to it through a global entry point such as Azure Front Door.");
        }

        // Prefer an explicit compute environment binding, then fall back to the deployment target's
        // compute environment. This matches how endpoint references are resolved elsewhere
        // (Azure Front Door origins, Foundry hosted agents) so all call sites agree on "where is
        // this resource deployed".
        if (boundComputeEnvironments.Count == 1)
        {
            computeEnvironment = boundComputeEnvironments[0];
            return true;
        }

        // No explicit binding. The singular accessor still throws when the resource has several deployment
        // targets, because an unbound resource with more than one target is genuinely ambiguous — silently
        // picking one would resolve the endpoint to an arbitrary environment.
        computeEnvironment = resource.GetDeploymentTargetAnnotation()?.ComputeEnvironment;
        return computeEnvironment is not null;
    }
}
