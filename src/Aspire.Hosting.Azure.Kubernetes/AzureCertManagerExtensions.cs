// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Azure.Kubernetes;
using Aspire.Hosting.Kubernetes;

namespace Aspire.Hosting;

/// <summary>
/// Provides AKS-specific overloads for installing cert-manager into an
/// <see cref="AzureKubernetesEnvironmentResource"/>.
/// </summary>
public static class AzureCertManagerExtensions
{
    /// <summary>
    /// Installs cert-manager into the AKS cluster's underlying Kubernetes environment and
    /// returns a typed <see cref="CertManagerResource"/> that can host issuer resources.
    /// </summary>
    /// <param name="builder">The AKS environment resource builder.</param>
    /// <param name="name">The Aspire resource name for the cert-manager installation.</param>
    /// <param name="chartVersion">The cert-manager Helm chart version to install.
    /// Defaults to a pinned version validated against this Aspire build.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{CertManagerResource}"/> for chaining.</returns>
    /// <remarks>
    /// Delegates to <see cref="CertManagerExtensions.AddCertManager(IResourceBuilder{KubernetesEnvironmentResource}, string, string?)"/>
    /// against the underlying <see cref="AzureKubernetesEnvironmentResource.KubernetesEnvironment"/>,
    /// mirroring the pattern used by <see cref="AzureKubernetesIngressExtensions.AddHelmChart"/>.
    /// </remarks>
    [AspireExport(Description = "Installs cert-manager into an AKS environment")]
    public static IResourceBuilder<CertManagerResource> AddCertManager(
        this IResourceBuilder<AzureKubernetesEnvironmentResource> builder,
        [ResourceName] string name,
        string? chartVersion = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(name);

        var k8sEnvBuilder = builder.ApplicationBuilder.CreateResourceBuilder(builder.Resource.KubernetesEnvironment);
        return k8sEnvBuilder.AddCertManager(name, chartVersion);
    }
}
