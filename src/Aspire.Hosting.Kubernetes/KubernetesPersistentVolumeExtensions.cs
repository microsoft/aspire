// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Kubernetes;
using Aspire.Hosting.Kubernetes.Annotations;
using Aspire.Hosting.Kubernetes.Extensions;
using Microsoft.Extensions.DependencyInjection;

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
    [AspireExport("withStorageClassParam")]
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
    [AspireExport("withCapacityParam")]
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
    /// using name matching. The workload must declare a volume with
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
    /// To bind a workload that does not have a matching named mount (for
    /// example a <c>ProjectResource</c>), use the overload that accepts a
    /// <c>mountPath</c> instead. The generated pod uses an Aspire-managed
    /// <c>fsGroup</c> of <c>2000</c> with an <c>OnRootMismatch</c> change policy so
    /// non-root containers can access supported volumes without matching the image's
    /// primary group. Use
    /// <see cref="KubernetesServiceExtensions.PublishAsKubernetesService{T}(IResourceBuilder{T}, Action{KubernetesResource})"/>
    /// to customize the pod security context when a different group or policy is required.
    /// </remarks>
    /// <example>
    /// <code>
    /// var pgData = k8s.AddPersistentVolume("pg-data")
    ///     .WithStorageClass("managed-csi")
    ///     .WithCapacity("20Gi");
    ///
    /// var pg = builder.AddPostgres("pg")
    ///     .WithDataVolume("pg-data")     // ContainerMountAnnotation source = "pg-data"
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

        builder.WithAnnotation(new VolumeMountBindingAnnotation(volume.Resource.Name)
        {
            RunModeHostPathResolver = context =>
            {
                var store = context.ExecutionContext.Services.GetRequiredService<IAspireStore>();
                return KubernetesPersistentVolumeLocalStorage.GetOrCreatePath(store, volume.Resource);
            }
        });

        // This overload takes no env, but the name-match composition can still opt into the portable
        // path by spelling env on the mount instead:
        //   .WithVolume("data", "/srv/data", env: "DATA_PATH").WithPersistentVolume(pv)
        // That mount may be declared after this call, so whether the scoped name actually gets applied
        // is decided at finalization rather than here. See ApplyRunModeContainerVolumeName.
        var runModeContainerVolumeName = GetRunModeContainerVolumeName(builder, volume);
        builder.WithAnnotation(new KubernetesPersistentVolumeBindingAnnotation(
            volume.Resource,
            runModeContainerVolumeName: runModeContainerVolumeName));
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
    /// <remarks>
    /// The generated pod uses an Aspire-managed <c>fsGroup</c> of <c>2000</c> with
    /// an <c>OnRootMismatch</c> change policy so non-root containers can access
    /// supported volumes without matching the image's primary group. Use
    /// <see cref="KubernetesServiceExtensions.PublishAsKubernetesService{T}(IResourceBuilder{T}, Action{KubernetesResource})"/>
    /// to customize the pod security context when a different group or policy is required.
    /// </remarks>
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
    [OverloadResolutionPriority(1)]
    [AspireExportIgnore(Reason = "Polyglot AppHosts use the withKubernetesPersistentVolumeMount adapter.")]
    public static IResourceBuilder<T> WithPersistentVolume<T>(
        this IResourceBuilder<T> builder,
        IResourceBuilder<KubernetesPersistentVolumeResource> volume,
        string mountPath,
        bool isReadOnly = false)
        where T : IComputeResource
    {
        return WithPersistentVolumeCore(builder, volume, mountPath, isReadOnly, env: null);
    }

    /// <summary>
    /// Binds a workload to a Kubernetes <see cref="KubernetesPersistentVolumeResource"/>,
    /// mounts it at the specified path when deployed, and exposes the effective storage
    /// path through an environment variable.
    /// </summary>
    /// <typeparam name="T">A compute resource that supports environment variables.</typeparam>
    /// <param name="builder">The workload resource builder.</param>
    /// <param name="volume">The persistent volume resource to bind to.</param>
    /// <param name="mountPath">The path inside the deployed container where the volume is mounted.</param>
    /// <param name="env">The environment variable that receives the effective storage path.</param>
    /// <param name="isReadOnly">When <see langword="true"/>, mounts the deployed volume read-only.</param>
    /// <returns>The same builder for chaining.</returns>
    /// <remarks>
    /// In run mode, projects and executables receive a deterministic host directory under
    /// the AppHost's <see cref="IAspireStore"/>. Containers receive the in-container
    /// <paramref name="mountPath"/>. In publish and deploy modes, every workload receives
    /// <paramref name="mountPath"/>.
    /// </remarks>
    /// <example>
    /// <code>
    /// var data = k8s.AddPersistentVolume("data")
    ///     .WithCapacity("20Gi");
    ///
    /// builder.AddProject&lt;Projects.Api&gt;("api")
    ///     .WithPersistentVolume(data, "/srv/data", env: "DATA_PATH");
    /// </code>
    /// </example>
    [AspireExportIgnore(Reason = "Polyglot AppHosts use the withKubernetesPersistentVolumeMount adapter.")]
    public static IResourceBuilder<T> WithPersistentVolume<T>(
        this IResourceBuilder<T> builder,
        IResourceBuilder<KubernetesPersistentVolumeResource> volume,
        string mountPath,
        string env,
        bool isReadOnly = false)
        where T : IComputeResource, IResourceWithEnvironment
    {
        ArgumentException.ThrowIfNullOrEmpty(env);

        return WithPersistentVolumeCore(builder, volume, mountPath, isReadOnly, env);
    }

    /// <summary>
    /// Binds a workload to a Kubernetes persistent volume and optionally exposes the
    /// effective storage path through an environment variable.
    /// </summary>
    /// <ats-summary>Binds a workload to a Kubernetes persistent volume and mounts it at a path</ats-summary>
    /// <typeparam name="T">A compute resource.</typeparam>
    /// <param name="builder">The workload resource builder.</param>
    /// <param name="volume">The persistent volume resource to bind to.</param>
    /// <param name="mountPath">The path inside the deployed container where the volume is mounted.</param>
    /// <param name="isReadOnly">When true, mounts the deployed volume read-only.</param>
    /// <param name="env">An optional environment variable that receives the effective storage path.</param>
    /// <returns>The same builder for chaining.</returns>
    /// <ats-returns>The resource builder.</ats-returns>
    [AspireExport("withKubernetesPersistentVolumeMount")]
    internal static IResourceBuilder<T> WithPersistentVolumeMountForExport<T>(
        this IResourceBuilder<T> builder,
        IResourceBuilder<KubernetesPersistentVolumeResource> volume,
        string mountPath,
        bool isReadOnly = false,
        string? env = null)
        where T : IComputeResource
    {
        return WithPersistentVolumeCore(builder, volume, mountPath, isReadOnly, env);
    }

    private static IResourceBuilder<T> WithPersistentVolumeCore<T>(
        IResourceBuilder<T> builder,
        IResourceBuilder<KubernetesPersistentVolumeResource> volume,
        string mountPath,
        bool isReadOnly,
        string? env)
        where T : IComputeResource
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(volume);
        ArgumentException.ThrowIfNullOrEmpty(mountPath);

        if (env is not null)
        {
            ArgumentException.ThrowIfNullOrEmpty(env);

            if (builder.Resource is not IResourceWithEnvironment)
            {
                throw new InvalidOperationException(
                    $"Resource '{builder.Resource.Name}' does not support environment variables and cannot use the '{env}' volume path variable.");
            }
        }

        var runModeContainerVolumeName = GetRunModeContainerVolumeName(builder, volume);

        // Declare the mount and its binding directly rather than through WithVolume, because the polyglot
        // adapter only constrains T to IComputeResource while the env-carrying WithVolume overload also
        // requires IResourceWithEnvironment. The env value itself still comes from the shared binding
        // logic so the inner/outer loop decision lives in exactly one place.
        var binding = new VolumeMountBindingAnnotation(volume.Resource.Name)
        {
            EnvironmentVariableName = env,
            MountPath = mountPath,
            RunModeHostPathResolver = context =>
            {
                var store = context.ExecutionContext.Services.GetRequiredService<IAspireStore>();
                return KubernetesPersistentVolumeLocalStorage.GetOrCreatePath(store, volume.Resource);
            }
        };

        builder.WithAnnotation(new ContainerMountAnnotation(
            volume.Resource.Name,
            mountPath,
            ContainerMountType.Volume,
            isReadOnly));

        builder.WithAnnotation(binding);

        if (env is not null)
        {
            builder.WithAnnotation(new EnvironmentCallbackAnnotation(context =>
            {
                context.EnvironmentVariables[env] = binding.ResolvePath(context);
            }));
        }

        builder.WithAnnotation(new KubernetesPersistentVolumeBindingAnnotation(
            volume.Resource,
            env,
            runModeContainerVolumeName));

        return builder;
    }

    /// <summary>
    /// Computes the worktree-scoped local volume name for a run-mode container binding. This only
    /// builds the candidate; whether it is actually applied is decided at finalization, because the
    /// env opt-in can be spelled on a mount declared after this binding.
    /// </summary>
    private static string? GetRunModeContainerVolumeName<T>(
        IResourceBuilder<T> builder,
        IResourceBuilder<KubernetesPersistentVolumeResource> volume)
        where T : IComputeResource
    {
        if (!builder.ApplicationBuilder.ExecutionContext.IsRunMode || builder.Resource is not ContainerResource)
        {
            return null;
        }

        // Generate is builder-bound because it needs the application name and the AppHost path hash,
        // so the candidate has to be built here even though the decision happens later.
        var environmentName = volume.Resource.Parent.Name.ToKubernetesResourceName();
        return VolumeNameGenerator.Generate(volume, $"kubernetes-{environmentName}");
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
