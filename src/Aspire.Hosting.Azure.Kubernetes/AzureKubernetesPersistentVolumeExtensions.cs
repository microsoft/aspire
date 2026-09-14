// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Azure.Kubernetes;
using Aspire.Hosting.Kubernetes;

namespace Aspire.Hosting;

/// <summary>
/// Provides extension methods for adding Kubernetes persistent volumes to an
/// <see cref="AzureKubernetesEnvironmentResource"/>.
/// </summary>
[Experimental("ASPIRECOMPUTE002", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
public static class AzureKubernetesPersistentVolumeExtensions
{
    /// <summary>
    /// Adds a Kubernetes PersistentVolumeClaim resource to the application model for the
    /// specified AKS environment.
    /// </summary>
    /// <ats-summary>Adds a Kubernetes PersistentVolumeClaim resource to an AKS environment</ats-summary>
    /// <param name="builder">The AKS environment resource builder.</param>
    /// <param name="name">The name of the persistent volume resource.</param>
    /// <returns>A builder for the new <see cref="KubernetesPersistentVolumeResource"/>.</returns>
    /// <ats-returns>The resource builder.</ats-returns>
    /// <remarks>
    /// <para>
    /// The persistent volume is associated with the AKS environment's underlying Kubernetes
    /// environment and generates a <c>v1.PersistentVolumeClaim</c> in the Helm chart output.
    /// </para>
    /// <para>
    /// When no storage class is configured, the generated claim omits
    /// <c>spec.storageClassName</c> so the cluster's default storage class is used. A standard
    /// AKS cluster dynamically provisions an Azure managed disk for such claims. Use
    /// <see cref="KubernetesPersistentVolumeExtensions.WithStorageClass(IResourceBuilder{KubernetesPersistentVolumeResource}, string)"/>
    /// to select a different storage class explicitly.
    /// </para>
    /// </remarks>
    /// <ats-remarks />
    /// <example>
    /// <code>
    /// var aks = builder.AddAzureKubernetesEnvironment("aks");
    ///
    /// var data = aks.AddPersistentVolume("data")
    ///     .WithCapacity("20Gi");
    ///
    /// builder.AddProject&lt;Projects.Api&gt;("api")
    ///     .WithPersistentVolume(data, "/data");
    /// </code>
    /// </example>
    [AspireExport]
    public static IResourceBuilder<KubernetesPersistentVolumeResource> AddPersistentVolume(
        this IResourceBuilder<AzureKubernetesEnvironmentResource> builder,
        [ResourceName] string name)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(name);

        var k8sEnvBuilder = builder.ApplicationBuilder.CreateResourceBuilder(builder.Resource.KubernetesEnvironment);
        return k8sEnvBuilder.AddPersistentVolume(name);
    }
}
