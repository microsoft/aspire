// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting;

/// <summary>
/// Provides extension methods for adding volume-backed storage to compute resources.
/// </summary>
public static class VolumeResourceBuilderExtensions
{
    /// <summary>
    /// Adds a volume to a compute resource and exposes its effective path through an environment variable.
    /// </summary>
    /// <typeparam name="T">The resource type.</typeparam>
    /// <param name="builder">The resource builder.</param>
    /// <param name="name">The name of the volume.</param>
    /// <param name="target">The target path where the volume is mounted after publishing.</param>
    /// <param name="env">The environment variable that receives the effective volume path.</param>
    /// <param name="isReadOnly">A flag that indicates if the published volume should be mounted as read-only.</param>
    /// <returns>The <see cref="IResourceBuilder{T}"/>.</returns>
    /// <remarks>
    /// Containers receive <paramref name="target"/> in run and publish modes. Projects and
    /// executables receive a workload-scoped <see cref="IAspireStore"/> directory in run mode
    /// and <paramref name="target"/> in publish mode.
    /// Named storage is independent of the resource lifetime. Session resources stop with the
    /// AppHost and reuse their named storage on the next run; persistent resources can keep the
    /// compute instance alive and continue using the same storage. Cleaning the AppHost store can
    /// remove local project and executable data.
    /// </remarks>
    /// <example>
    /// <code language="csharp">
    /// builder.AddProject&lt;Projects.Api&gt;("api")
    ///     .WithVolume("data", "/usr/data", env: "DATA_PATH");
    /// </code>
    /// </example>
    [AspireExportIgnore(Reason = "Polyglot export is via CoreExports.WithVolume which reorders parameters.")]
    public static IResourceBuilder<T> WithVolume<T>(
        this IResourceBuilder<T> builder,
        string name,
        string target,
        string env,
        bool isReadOnly = false)
        where T : IComputeResource, IResourceWithEnvironment
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentException.ThrowIfNullOrEmpty(target);
        ArgumentException.ThrowIfNullOrEmpty(env);

        return WithVolumeCore(builder, name, target, isReadOnly, env);
    }

    internal static IResourceBuilder<T> WithVolumeCore<T>(
        IResourceBuilder<T> builder,
        string? name,
        string target,
        bool isReadOnly,
        string? env,
        Func<EnvironmentCallbackContext, string>? getRunModeHostPath = null)
        where T : IComputeResource
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(target);

        if (env is not null)
        {
            ArgumentException.ThrowIfNullOrEmpty(env);

            if (builder.Resource is ProjectResource or ExecutableResource)
            {
                ArgumentException.ThrowIfNullOrEmpty(name);
            }

            if (builder.Resource is not IResourceWithEnvironment)
            {
                throw new InvalidOperationException(
                    $"Resource '{builder.Resource.Name}' does not support environment variables and cannot use the '{env}' volume path variable.");
            }
        }

        builder.WithAnnotation(new ContainerMountAnnotation(name, target, ContainerMountType.Volume, isReadOnly));

        // Restate the binding declaratively. The env callback below captures env in a closure, so a
        // compute environment inspecting the model afterwards cannot otherwise tell that this mount
        // resolves a local path in run mode.
        VolumeMountBindingAnnotation? binding = null;

        if (name is not null && (env is not null || getRunModeHostPath is not null))
        {
            binding = new VolumeMountBindingAnnotation(name)
            {
                EnvironmentVariableName = env,
                MountPath = target,
                RunModeHostPathResolver = getRunModeHostPath
            };

            builder.WithAnnotation(binding);
        }

        if (env is not null)
        {
            builder.WithAnnotation(new EnvironmentCallbackAnnotation(context =>
            {
                if (binding is not null)
                {
                    context.EnvironmentVariables[env] = binding.ResolvePath(context);
                    return;
                }

                // Anonymous volumes have no name to scope a local directory by, so only the deployed
                // mount path and a container's own mount are resolvable.
                if (context.ExecutionContext.IsPublishMode || context.Resource is ContainerResource)
                {
                    if (context.ExecutionContext.IsPublishMode)
                    {
                        VolumeMountBindingAnnotation.ThrowIfEnvironmentCannotMount(context, volumeName: null, env);
                    }

                    context.EnvironmentVariables[env] = target;
                    return;
                }

                throw new InvalidOperationException(
                    $"Resource '{context.Resource.Name}' cannot resolve the '{env}' volume path in run mode because the volume is anonymous.");
            }));
        }

        return builder;
    }
}
