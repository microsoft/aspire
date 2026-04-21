// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Kubernetes;

/// <summary>
/// Provides context for configuring Kubernetes ingress for a resource with external HTTP endpoints.
/// </summary>
public sealed class KubernetesIngressContext
{
    /// <summary>
    /// Gets the Kubernetes resource being configured. Use <see cref="KubernetesResource.AdditionalResources"/>
    /// to add <see cref="Resources.Ingress"/> or other Kubernetes objects.
    /// </summary>
    public required KubernetesResource KubernetesResource { get; init; }

    /// <summary>
    /// Gets the original Aspire resource from the application model.
    /// </summary>
    public required IResource Resource { get; init; }

    /// <summary>
    /// Gets the Kubernetes environment this resource is being deployed to.
    /// </summary>
    public required KubernetesEnvironmentResource Environment { get; init; }

    /// <summary>
    /// Gets the external HTTP endpoint annotations that need ingress configuration.
    /// This list is pre-filtered to only include endpoints where
    /// <see cref="EndpointAnnotation.IsExternal"/> is <c>true</c> and the scheme is HTTP or HTTPS.
    /// </summary>
    public required IReadOnlyList<EndpointAnnotation> ExternalHttpEndpoints { get; init; }
}
