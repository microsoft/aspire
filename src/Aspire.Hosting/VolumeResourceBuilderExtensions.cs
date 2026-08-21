// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.DependencyInjection;

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

        if (name is not null && getRunModeHostPath is not null)
        {
            AddRunModePathResolver(builder, name, getRunModeHostPath);
        }

        if (env is not null)
        {
            if (name is not null)
            {
                // Restate the env binding declaratively. The callback below captures env in a closure,
                // so a compute environment inspecting the model afterwards cannot otherwise tell that
                // this mount resolves a local path in run mode.
                builder.WithAnnotation(new VolumeEnvironmentVariableAnnotation(name, env));
            }

            builder.WithAnnotation(new EnvironmentCallbackAnnotation(context =>
            {
                context.EnvironmentVariables[env] = GetEffectiveVolumePath(context, name, target, env);
            }));
        }

        return builder;
    }

    internal static IResourceBuilder<T> AddRunModePathResolver<T>(
        IResourceBuilder<T> builder,
        string volumeName,
        Func<EnvironmentCallbackContext, string> resolver)
        where T : IComputeResource
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(volumeName);
        ArgumentNullException.ThrowIfNull(resolver);

        return builder.WithAnnotation(new VolumeMountPathResolverAnnotation(volumeName, resolver));
    }

    private static string GetEffectiveVolumePath(
        EnvironmentCallbackContext context,
        string? name,
        string target,
        string env)
    {
        if (context.ExecutionContext.IsPublishMode || context.Resource is ContainerResource)
        {
            return target;
        }

        if (name is null)
        {
            throw new InvalidOperationException(
                $"Resource '{context.Resource.Name}' cannot resolve the '{env}' volume path in run mode because the volume is anonymous.");
        }

        var resolver = context.Resource.Annotations
            .OfType<VolumeMountPathResolverAnnotation>()
            .LastOrDefault(annotation => string.Equals(annotation.VolumeName, name, StringComparison.Ordinal))
            ?.Resolver;

        if (resolver is not null)
        {
            return resolver(context);
        }

        // Containers already returned above, so everything remaining runs as a host process and needs
        // a local directory. Projects and executables are the in-box cases, but the public overload
        // accepts any IComputeResource, so custom compute resources resolve here too. Throwing instead
        // would let a call that compiles cleanly fail much later during environment evaluation.
        var store = context.ExecutionContext.Services.GetRequiredService<IAspireStore>();
        return VolumeMountPathResolver.GetOrCreateLocalPath(store, context.Resource, name);
    }
}
