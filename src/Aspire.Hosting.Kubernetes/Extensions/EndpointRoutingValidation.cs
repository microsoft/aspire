// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Kubernetes.Extensions;

/// <summary>
/// Helpers that validate publish-time intent for endpoints that are being
/// routed by ingress / gateway-style resources.
/// </summary>
/// <remarks>
/// Ingress and Gateway resources expose their backing service to traffic that
/// originates outside the cluster, so it is a privacy/security footgun to
/// route an <see cref="EndpointReference"/> that the resource owner never
/// flagged as external. The check is performed during publish (when the
/// Helm chart is materialized) rather than at <c>WithPath</c> / <c>WithRoute</c> call time
/// because authoring order is not significant: a user may legitimately
/// register the path/route before calling
/// <see cref="ResourceBuilderExtensions.WithExternalHttpEndpoints{T}"/> or
/// setting <see cref="EndpointAnnotation.IsExternal"/> directly.
/// </remarks>
internal static class EndpointRoutingValidation
{
    /// <summary>
    /// Throws an <see cref="InvalidOperationException"/> when the supplied
    /// endpoint is not marked external on its <see cref="EndpointAnnotation"/>.
    /// </summary>
    /// <param name="endpoint">The routed endpoint reference.</param>
    /// <param name="routingResourceKind">The kind of routing resource (e.g., <c>"Ingress"</c> or <c>"Gateway"</c>).</param>
    /// <param name="routingResourceName">The name of the routing resource that owns the route.</param>
    public static void ThrowIfEndpointNotExternal(
        EndpointReference endpoint,
        string routingResourceKind,
        string routingResourceName)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentException.ThrowIfNullOrEmpty(routingResourceKind);
        ArgumentException.ThrowIfNullOrEmpty(routingResourceName);

        // EndpointAnnotation captures publish-time intent — the endpoint
        // is external if and only if the author explicitly opted in, either
        // through .WithExternalHttpEndpoints() or by passing isExternal: true
        // when creating the endpoint annotation.
        if (endpoint.EndpointAnnotation.IsExternal)
        {
            return;
        }

        throw new InvalidOperationException(
            $"Resource '{endpoint.Resource.Name}' endpoint '{endpoint.EndpointName}' is not marked as external " +
            $"but is being routed by {routingResourceKind} '{routingResourceName}'. " +
            $"Call .WithExternalHttpEndpoints() on the target resource or pass isExternal: true when " +
            $"creating the endpoint annotation. {routingResourceKind} routes may only expose endpoints " +
            $"that are explicitly marked external.");
    }
}
