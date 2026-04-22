// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Kubernetes;

/// <summary>
/// An annotation that stores a custom domain hostname for a resource's Kubernetes Ingress.
/// When present, the Ingress is configured with a <c>host</c> rule and <c>tls</c> section
/// for automatic certificate management via cert-manager.
/// </summary>
/// <param name="hostname">The custom domain hostname (e.g., <c>app.contoso.com</c>).</param>
public sealed class KubernetesCustomDomainAnnotation(string hostname) : IResourceAnnotation
{
    /// <summary>
    /// Gets the custom domain hostname.
    /// </summary>
    public string Hostname { get; } = hostname;
}
