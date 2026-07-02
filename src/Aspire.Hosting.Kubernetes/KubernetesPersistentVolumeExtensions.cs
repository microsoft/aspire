// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Kubernetes;
using Aspire.Hosting.Kubernetes.Annotations;

namespace Aspire.Hosting;

/// <summary>
/// Provides extension methods for configuring Kubernetes
/// <see cref="KubernetesPersistentVolumeResource"/> resources and binding workloads
/// to them.
/// </summary>
[Experimental("ASPIRECOMPUTE002", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
public static class KubernetesPersistentVolumeExtensions
{
    /// <summary>
    /// Adds a Kubernetes PersistentVolumeClaim resource to the application model as a
    /// child of the specified Kubernetes environment. The resource generates a
    /// <c>v1.PersistentVolumeClaim</c> manifest in the Helm chart output at publish
    /// time.
    /// </summary>
    /// <ats-summary>Adds a Kubernetes PersistentVolumeClaim resource</ats-summary>
    /// <param name="builder">The Kubernetes environment resource builder.</param>
    /// <param name="name">The name of the persistent volume resource. Used as the
    /// generated PVC's <c>metadata.name</c> after lower-casing. To bind a workload
    /// using the name-match overload of
    /// <see cref="WithPersistentVolume{T}(IResourceBuilder{T}, IResourceBuilder{KubernetesPersistentVolumeResource})"/>,
    /// add a <c>WithVolume("name", "/path")</c> on the workload using the same
    /// <paramref name="name"/>.</param>
    /// <returns>A builder for the new <see cref="KubernetesPersistentVolumeResource"/>.</returns>
    /// <example>
    /// <code>
    /// var k8s = builder.AddKubernetesEnvironment("k8s");
    /// var data = k8s.AddPersistentVolume("data")
    ///     .WithStorageClass("managed-csi")
    ///     .WithCapacity("20Gi");
    /// </code>
    /// </example>
    [AspireExport]
    public static IResourceBuilder<KubernetesPersistentVolumeResource> AddPersistentVolume(
        this IResourceBuilder<KubernetesEnvironmentResource> builder,
        [ResourceName] string name)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(name);

        var volume = new KubernetesPersistentVolumeResource(name, builder.Resource);

        if (builder.ApplicationBuilder.ExecutionContext.IsRunMode)
        {
            // Persistent volumes are publish-only — surface them in the model but skip
            // manifest generation in run mode (mirrors the ingress and gateway pattern).
            return builder.ApplicationBuilder.CreateResourceBuilder(volume);
        }

        return builder.ApplicationBuilder.AddResource(volume)
            .ExcludeFromManifest();
    }

    /// <summary>
    /// Sets the Kubernetes storage class name on the PVC's
    /// <c>spec.storageClassName</c>. When unset, the cluster's default storage class
    /// is used.
    /// </summary>
    /// <ats-summary>Sets the storage class for a persistent volume</ats-summary>
    /// <param name="builder">The persistent volume resource builder.</param>
    /// <param name="storageClassName">The storage class name (e.g.
    /// <c>"managed-csi"</c>, <c>"gp3"</c>).</param>
    /// <returns>The same builder for chaining.</returns>
    [AspireExport]
    public static IResourceBuilder<KubernetesPersistentVolumeResource> WithStorageClass(
        this IResourceBuilder<KubernetesPersistentVolumeResource> builder,
        string storageClassName)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(storageClassName);

        builder.Resource.StorageClassName = ReferenceExpression.Create($"{storageClassName}");
        return builder;
    }

    /// <summary>
    /// Sets the Kubernetes storage class name using a parameter resolved at deploy
    /// time.
    /// </summary>
    /// <ats-summary>Sets a parameterized storage class for a persistent volume</ats-summary>
    /// <param name="builder">The persistent volume resource builder.</param>
    /// <param name="storageClassName">A parameter resource builder for the storage
    /// class name.</param>
    /// <returns>The same builder for chaining.</returns>
    [AspireExport("withPvStorageClassParam")]
    public static IResourceBuilder<KubernetesPersistentVolumeResource> WithStorageClass(
        this IResourceBuilder<KubernetesPersistentVolumeResource> builder,
        IResourceBuilder<ParameterResource> storageClassName)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(storageClassName);

        builder.Resource.StorageClassName = ReferenceExpression.Create($"{storageClassName.Resource}");
        return builder;
    }

    /// <summary>
    /// Sets the requested storage capacity on the PVC's
    /// <c>spec.resources.requests.storage</c> field.
    /// </summary>
    /// <ats-summary>Sets the requested storage capacity for a persistent volume</ats-summary>
    /// <param name="builder">The persistent volume resource builder.</param>
    /// <param name="capacity">A Kubernetes quantity string (e.g. <c>"10Gi"</c>,
    /// <c>"500Mi"</c>).</param>
    /// <returns>The same builder for chaining.</returns>
    [AspireExport]
    public static IResourceBuilder<KubernetesPersistentVolumeResource> WithCapacity(
        this IResourceBuilder<KubernetesPersistentVolumeResource> builder,
        string capacity)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(capacity);

        builder.Resource.Capacity = ReferenceExpression.Create($"{capacity}");
        return builder;
    }

    /// <summary>
    /// Sets the requested storage capacity using a parameter resolved at deploy time.
    /// </summary>
    /// <ats-summary>Sets a parameterized storage capacity for a persistent volume</ats-summary>
    /// <param name="builder">The persistent volume resource builder.</param>
    /// <param name="capacity">A parameter resource builder for the capacity quantity
    /// string.</param>
    /// <returns>The same builder for chaining.</returns>
    [AspireExport("withPvCapacityParam")]
    public static IResourceBuilder<KubernetesPersistentVolumeResource> WithCapacity(
        this IResourceBuilder<KubernetesPersistentVolumeResource> builder,
        IResourceBuilder<ParameterResource> capacity)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(capacity);

        builder.Resource.Capacity = ReferenceExpression.Create($"{capacity.Resource}");
        return builder;
    }

    /// <summary>
    /// Adds an access mode to the PVC's <c>spec.accessModes</c>. Call multiple times
    /// to declare more than one mode. When unset, the environment's
    /// <see cref="KubernetesEnvironmentResource.DefaultStorageReadWritePolicy"/> is
    /// used.
    /// </summary>
    /// <ats-summary>Adds an access mode to a persistent volume</ats-summary>
    /// <param name="builder">The persistent volume resource builder.</param>
    /// <param name="accessMode">The access mode to add.</param>
    /// <returns>The same builder for chaining.</returns>
    [AspireExport]
    public static IResourceBuilder<KubernetesPersistentVolumeResource> WithAccessMode(
        this IResourceBuilder<KubernetesPersistentVolumeResource> builder,
        PersistentVolumeAccessMode accessMode)
    {
        ArgumentNullException.ThrowIfNull(builder);

        if (!builder.Resource.AccessModes.Contains(accessMode))
        {
            builder.Resource.AccessModes.Add(accessMode);
        }

        return builder;
    }

    /// <summary>
    /// Adds a Kubernetes metadata annotation to the generated PVC. These flush to
    /// <c>metadata.annotations</c> on the rendered Kubernetes resource — not Aspire
    /// <see cref="ApplicationModel.IResourceAnnotation"/> instances. Common uses:
    /// CSI driver hints, dynamic provisioner parameters, external-secrets selectors,
    /// or backup tooling tags.
    /// </summary>
    /// <ats-summary>Adds a Kubernetes metadata annotation to a persistent volume</ats-summary>
    /// <param name="builder">The persistent volume resource builder.</param>
    /// <param name="key">The annotation key (e.g.
    /// <c>"volume.beta.kubernetes.io/storage-provisioner"</c>).</param>
    /// <param name="value">The annotation value.</param>
    /// <returns>The same builder for chaining.</returns>
    [AspireExport]
    public static IResourceBuilder<KubernetesPersistentVolumeResource> WithVolumeAnnotation(
        this IResourceBuilder<KubernetesPersistentVolumeResource> builder,
        string key,
        string value)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(key);
        ArgumentNullException.ThrowIfNull(value);

        builder.Resource.VolumeAnnotations[key] = ReferenceExpression.Create($"{value}");
        return builder;
    }

    /// <summary>
    /// Adds a Kubernetes metadata annotation with a parameter value resolved at
    /// deploy time.
    /// </summary>
    /// <ats-summary>Adds a parameterized Kubernetes metadata annotation to a persistent volume</ats-summary>
    /// <param name="builder">The persistent volume resource builder.</param>
    /// <param name="key">The annotation key.</param>
    /// <param name="value">A parameter resource builder for the annotation value.</param>
    /// <returns>The same builder for chaining.</returns>
    [AspireExport("withVolumeAnnotationParam")]
    public static IResourceBuilder<KubernetesPersistentVolumeResource> WithVolumeAnnotation(
        this IResourceBuilder<KubernetesPersistentVolumeResource> builder,
        string key,
        IResourceBuilder<ParameterResource> value)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(key);
        ArgumentNullException.ThrowIfNull(value);

        builder.Resource.VolumeAnnotations[key] = ReferenceExpression.Create($"{value.Resource}");
        return builder;
    }

    /// <summary>
    /// Binds a workload to a Kubernetes <see cref="KubernetesPersistentVolumeResource"/>
    /// using <em>name matching</em>. The workload must already declare a volume with
    /// a matching <c>source</c> name (typically via <c>WithVolume("name", "/path")</c>
    /// or an integration helper such as Postgres'
    /// <c>WithDataVolume()</c>). The publisher rewrites that volume's pod-spec entry
    /// to reference the generated PVC and promotes the workload to a
    /// <c>StatefulSet</c>.
    /// </summary>
    /// <ats-summary>Binds a workload to a Kubernetes persistent volume by matching volume name</ats-summary>
    /// <typeparam name="T">A compute resource (container, project, executable).</typeparam>
    /// <param name="builder">The workload resource builder.</param>
    /// <param name="volume">The persistent volume resource to bind to.</param>
    /// <returns>The same builder for chaining.</returns>
    /// <remarks>
    /// To bind a workload that does not already have a matching named mount (for
    /// example a <c>ProjectResource</c>), use the overload that accepts a
    /// <c>mountPath</c> instead.
    /// </remarks>
    /// <example>
    /// <code>
    /// var pgData = k8s.AddPersistentVolume("pg-data")
    ///     .WithStorageClass("managed-csi")
    ///     .WithCapacity("20Gi");
    ///
    /// var pg = builder.AddPostgres("pg")
    ///     .WithDataVolume()              // ContainerMountAnnotation source = "pg-data"
    ///     .WithPersistentVolume(pgData); // matches by name "pg-data"
    /// </code>
    /// </example>
    [AspireExport("withKubernetesPersistentVolume")]
    public static IResourceBuilder<T> WithPersistentVolume<T>(
        this IResourceBuilder<T> builder,
        IResourceBuilder<KubernetesPersistentVolumeResource> volume)
        where T : IComputeResource
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(volume);

        builder.WithAnnotation(new KubernetesPersistentVolumeBindingAnnotation(volume.Resource));
        return builder;
    }

    /// <summary>
    /// Binds a workload to a Kubernetes <see cref="KubernetesPersistentVolumeResource"/>
    /// and mounts it at the specified path inside the workload's container. Unlike
    /// the name-match overload this one creates the underlying mount itself, so it
    /// works for workloads that don't already declare a named volume — including
    /// <see cref="ProjectResource"/>.
    /// </summary>
    /// <ats-summary>Binds a workload to a Kubernetes persistent volume and mounts it at a path</ats-summary>
    /// <typeparam name="T">A compute resource (container, project, executable).</typeparam>
    /// <param name="builder">The workload resource builder.</param>
    /// <param name="volume">The persistent volume resource to bind to.</param>
    /// <param name="mountPath">The path inside the container where the volume will
    /// be mounted (e.g. <c>"/var/lib/postgresql/data"</c>).</param>
    /// <param name="isReadOnly">When <see langword="true"/>, mounts the volume
    /// read-only.</param>
    /// <returns>The same builder for chaining.</returns>
    /// <example>
    /// <code>
    /// var media = k8s.AddPersistentVolume("media")
    ///     .WithStorageClass("azurefile-csi")
    ///     .WithCapacity("100Gi")
    ///     .WithAccessMode(PersistentVolumeAccessMode.ReadWriteMany);
    ///
    /// builder.AddProject&lt;MyApi&gt;("api")
    ///        .WithPersistentVolume(media, "/srv/media");
    /// </code>
    /// </example>
    [AspireExport("withKubernetesPersistentVolumeMount")]
    public static IResourceBuilder<T> WithPersistentVolume<T>(
        this IResourceBuilder<T> builder,
        IResourceBuilder<KubernetesPersistentVolumeResource> volume,
        string mountPath,
        bool isReadOnly = false)
        where T : IComputeResource
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(volume);
        ArgumentException.ThrowIfNullOrEmpty(mountPath);

        builder.WithAnnotation(new ContainerMountAnnotation(volume.Resource.Name, mountPath, ContainerMountType.Volume, isReadOnly));
        builder.WithAnnotation(new KubernetesPersistentVolumeBindingAnnotation(volume.Resource));
        return builder;
    }

    /// <summary>
    /// Converts a <see cref="PersistentVolumeAccessMode"/> enum value to the
    /// Kubernetes API string representation.
    /// </summary>
    internal static string ToKubernetesString(this PersistentVolumeAccessMode accessMode)
    {
        return accessMode switch
        {
            PersistentVolumeAccessMode.ReadWriteOnce => "ReadWriteOnce",
            PersistentVolumeAccessMode.ReadOnlyMany => "ReadOnlyMany",
            PersistentVolumeAccessMode.ReadWriteMany => "ReadWriteMany",
            PersistentVolumeAccessMode.ReadWriteOncePod => "ReadWriteOncePod",
            _ => throw new ArgumentOutOfRangeException(nameof(accessMode), accessMode, "Unknown persistent volume access mode."),
        };
    }
}
