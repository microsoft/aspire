// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// Describes how a named volume binds to a resource across the inner and outer loop.
/// </summary>
/// <remarks>
/// <para>
/// This annotation is the extensibility point compute environments use to participate in the portable
/// volume path convention. It carries two independent facets, either of which may be absent:
/// </para>
/// <list type="bullet">
/// <item><description>
/// <see cref="EnvironmentVariableName"/> records that the workload reads the effective storage path from
/// an environment variable. The variable itself is written by an <see cref="EnvironmentCallbackAnnotation"/>
/// whose closure captures the name, which makes the intent invisible to anything inspecting the model.
/// Restating it here lets a compute environment tell whether a host process materializes a local backing
/// store for the volume, without having to observe the callback running.
/// </description></item>
/// <item><description>
/// <see cref="RunModeHostPathResolver"/> lets a compute environment supply the local directory that backs
/// the volume in run mode. Without it, host processes fall back to a workload-scoped directory under
/// <see cref="IAspireStore"/>.
/// </description></item>
/// </list>
/// <para>
/// The two facets are produced by different parties at different times — the AppHost author opts into the
/// environment variable, while the compute environment supplies the local path — so a resource can carry
/// several of these annotations for the same <see cref="VolumeName"/>. A binding resolves through its own
/// <see cref="RunModeHostPathResolver"/> when it has one, and otherwise takes the last sibling that does.
/// <see cref="VolumeName"/> alone is not a unique key, because separate compute environments can each
/// declare a volume under the same name.
/// </para>
/// </remarks>
public sealed class VolumeMountBindingAnnotation(string volumeName) : IResourceAnnotation
{
    /// <summary>
    /// Gets the name of the volume this binding applies to.
    /// </summary>
    public string VolumeName { get; } = ThrowIfNullOrEmpty(volumeName);

    /// <summary>
    /// Gets the environment variable that receives the effective storage path, or <see langword="null"/>
    /// when this binding only supplies a run-mode path.
    /// </summary>
    public string? EnvironmentVariableName { get; init; }

    /// <summary>
    /// Gets the path the volume is mounted at once deployed, or <see langword="null"/> when this binding
    /// only supplies a run-mode path for a mount declared elsewhere.
    /// </summary>
    public string? MountPath { get; init; }

    /// <summary>
    /// Gets a callback that returns the local host directory backing the volume in run mode, or
    /// <see langword="null"/> to use the default workload-scoped directory under <see cref="IAspireStore"/>.
    /// </summary>
    public Func<EnvironmentCallbackContext, string>? RunModeHostPathResolver { get; init; }

    /// <summary>
    /// Resolves the storage path the workload should use for the current execution mode.
    /// </summary>
    /// <param name="context">The environment callback context being evaluated.</param>
    /// <returns>
    /// <see cref="MountPath"/> when publishing or when the workload runs as a container, and otherwise a
    /// local host directory.
    /// </returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the deployed mount path is required but this binding does not declare one.
    /// </exception>
    public string ResolvePath(EnvironmentCallbackContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.ExecutionContext.IsPublishMode || context.Resource is ContainerResource)
        {
            var mountPath = MountPath ?? throw new InvalidOperationException(
                $"Volume '{VolumeName}' on resource '{context.Resource.Name}' does not declare a mount path.");

            if (context.ExecutionContext.IsPublishMode)
            {
                ThrowIfEnvironmentCannotMount(context);
            }

            return mountPath;
        }

        // Prefer this binding's own resolver. The sibling scan below only exists for the name-match
        // composition, where the mount and the compute environment binding are spelled as two separate
        // calls and the mount-declaring annotation therefore carries no resolver of its own.
        //
        // Scanning unconditionally would alias distinct volumes, because VolumeName is not a unique key —
        // two compute environments can each declare a volume under the same name, and every binding would
        // then select the last resolver and point at one environment's store. Aspire.Hosting.Kubernetes
        // rejects that shape up front, but this annotation is public and shared across compute
        // environments, so it resolves correctly on its own rather than relying on any one of them.
        var resolver = RunModeHostPathResolver ?? context.Resource.Annotations
            .OfType<VolumeMountBindingAnnotation>()
            .LastOrDefault(annotation =>
                annotation.RunModeHostPathResolver is not null &&
                string.Equals(annotation.VolumeName, VolumeName, StringComparison.Ordinal))
            ?.RunModeHostPathResolver;

        if (resolver is not null)
        {
            return resolver(context);
        }

        // Containers already returned above, so everything remaining runs as a host process and needs
        // a local directory. Projects and executables are the in-box cases, but the public overload
        // accepts any IComputeResource, so custom compute resources resolve here too. Throwing instead
        // would let a call that compiles cleanly fail much later during environment evaluation.
        var store = context.ExecutionContext.Services.GetRequiredService<IAspireStore>();
        return VolumeMountPathResolver.GetOrCreateLocalPath(store, context.Resource, VolumeName);
    }

    /// <summary>
    /// Throws when the resource is published to a compute environment that cannot back the volume with
    /// real storage.
    /// </summary>
    private void ThrowIfEnvironmentCannotMount(EnvironmentCallbackContext context)
    {
        // Only an environment that consumes ContainerMountAnnotation can back the path handed to the
        // workload. When the environment is known and does not, the variable resolves to ordinary
        // container storage: writes succeed and are then lost on restart. Fail at publish time instead,
        // because nothing downstream surfaces the problem.
        //
        // A null environment means the model has no compute environment, or several with no explicit
        // binding. Those are ambiguous rather than known-unsupported, so stay quiet rather than block a
        // publish that may well be fine.
        if (context.Resource.GetComputeEnvironment() is not { } environment ||
            environment is IComputeEnvironmentWithVolumeMounts)
        {
            return;
        }

        throw new InvalidOperationException(
            $"Resource '{context.Resource.Name}' binds volume '{VolumeName}' to environment variable " +
            $"'{EnvironmentVariableName}', but compute environment '{environment.Name}' does not support volume " +
            $"mounts. The variable would point at a path that is not backed by storage, so anything written there " +
            $"is lost when the workload restarts. Remove the environment variable binding, or target a compute " +
            $"environment that supports volume mounts.");
    }

    private static string ThrowIfNullOrEmpty([NotNull] string? argument, [CallerArgumentExpression(nameof(argument))] string? paramName = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(argument, paramName);
        return argument;
    }
}
