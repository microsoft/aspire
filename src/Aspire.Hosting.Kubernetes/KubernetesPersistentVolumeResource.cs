// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Kubernetes.Resources;

namespace Aspire.Hosting.Kubernetes;

/// <summary>
/// Represents a Kubernetes PersistentVolumeClaim as a first-class resource in the
/// Aspire application model. A persistent volume resource carries the storage class,
/// capacity, access modes, and metadata annotations needed to render a
/// <c>v1.PersistentVolumeClaim</c> at publish time. Workloads bind to it with
/// <see cref="KubernetesPersistentVolumeExtensions.WithPersistentVolume{T}(IResourceBuilder{T}, IResourceBuilder{KubernetesPersistentVolumeResource})"/>.
/// </summary>
/// <param name="name">The name of the persistent volume resource. Used as the
/// generated <c>PersistentVolumeClaim.metadata.name</c> after lower-casing.</param>
/// <param name="environment">The parent Kubernetes environment resource.</param>
/// <remarks>
/// <para>
/// Bind a workload to the volume by either:
/// </para>
/// <list type="bullet">
/// <item><description>
/// Adding a matching <c>WithVolume("name", "/mount/path")</c> on a container resource
/// and then calling <c>WithPersistentVolume(volume)</c>. The publisher matches by
/// volume name and routes the pod's <c>volumes[]</c> entry through this resource's
/// generated PVC.
/// </description></item>
/// <item><description>
/// Calling the
/// <see cref="KubernetesPersistentVolumeExtensions.WithPersistentVolume{T}(IResourceBuilder{T}, IResourceBuilder{KubernetesPersistentVolumeResource}, string, bool)"/>
/// overload that takes a mount path. Works for both <c>ContainerResource</c> and
/// <c>ProjectResource</c>.
/// </description></item>
/// </list>
/// <para>
/// Any workload bound to a persistent volume is automatically rendered as a
/// <c>StatefulSet</c> rather than a <c>Deployment</c> — Kubernetes requires
/// stable identity and ordered rollout for pods that share named PVCs.
/// </para>
/// <para>
/// This resource is publish-only. It has no run-mode behavior or dashboard surface.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// var k8s = builder.AddKubernetesEnvironment("k8s");
///
/// var data = k8s.AddPersistentVolume("data")
///     .WithStorageClass("managed-csi")
///     .WithCapacity("20Gi")
///     .WithAccessMode(PersistentVolumeAccessMode.ReadWriteOnce)
///     .WithVolumeAnnotation("volume.beta.kubernetes.io/storage-provisioner", "disk.csi.azure.com");
///
/// builder.AddContainer("postgres", "postgres:16")
///        .WithVolume("data", "/var/lib/postgresql/data")
///        .WithPersistentVolume(data);
/// </code>
/// </example>
[Experimental("ASPIRECOMPUTE002", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
public sealed class KubernetesPersistentVolumeResource(
    string name,
    KubernetesEnvironmentResource environment) : Resource(name), IResourceWithParent<KubernetesEnvironmentResource>
{
    /// <summary>
    /// Gets the parent Kubernetes environment resource.
    /// </summary>
    public KubernetesEnvironmentResource Parent { get; } = environment ?? throw new ArgumentNullException(nameof(environment));

    /// <summary>
    /// Gets or sets the storage class name for the generated PVC. When unset, the
    /// cluster's default storage class is used.
    /// </summary>
    internal ReferenceExpression? StorageClassName { get; set; }

    /// <summary>
    /// Gets or sets the requested storage capacity for the generated PVC (e.g.
    /// <c>"10Gi"</c>). When unset, falls back to
    /// <see cref="KubernetesEnvironmentResource.DefaultStorageSize"/>.
    /// </summary>
    internal ReferenceExpression? Capacity { get; set; }

    /// <summary>
    /// Gets the access modes configured for the volume. When empty, falls back to
    /// <see cref="KubernetesEnvironmentResource.DefaultStorageReadWritePolicy"/>.
    /// </summary>
    internal List<PersistentVolumeAccessMode> AccessModes { get; } = [];

    /// <summary>
    /// Gets the Kubernetes metadata annotations to add to the generated PVC. These
    /// are key-value pairs placed in the <c>metadata.annotations</c> field of the
    /// rendered Kubernetes resource — not Aspire <see cref="IResourceAnnotation"/>
    /// instances. CSI drivers, dynamic provisioners, and external operators
    /// (external-secrets, cert-manager, etc.) can pick them up at deploy time.
    /// </summary>
    internal Dictionary<string, ReferenceExpression> VolumeAnnotations { get; } = [];

    /// <summary>
    /// Gets the generated <see cref="PersistentVolumeClaim"/> for this resource,
    /// populated during publish processing. <see langword="null"/> until publish runs.
    /// </summary>
    internal PersistentVolumeClaim? GeneratedClaim { get; set; }
}

/// <summary>
/// Specifies how a persistent volume may be mounted by pods. Maps directly to the
/// Kubernetes <c>PersistentVolumeAccessMode</c> values.
/// </summary>
[Experimental("ASPIRECOMPUTE002", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
public enum PersistentVolumeAccessMode
{
    /// <summary>
    /// The volume can be mounted as read-write by a single node. Most common for
    /// block storage backed databases.
    /// </summary>
    ReadWriteOnce,

    /// <summary>
    /// The volume can be mounted as read-only by many nodes simultaneously.
    /// </summary>
    ReadOnlyMany,

    /// <summary>
    /// The volume can be mounted as read-write by many nodes simultaneously.
    /// Typically used for shared file stores (e.g. Azure Files, NFS).
    /// </summary>
    ReadWriteMany,

    /// <summary>
    /// The volume can be mounted as read-write by a single pod. Requires Kubernetes
    /// 1.27 or later (<c>ReadWriteOncePod</c> access mode).
    /// </summary>
    ReadWriteOncePod,
}
