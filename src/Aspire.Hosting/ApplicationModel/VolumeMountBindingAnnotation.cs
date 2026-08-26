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
/// several of these annotations for the same <see cref="VolumeName"/>. Lookups take the last match, which
/// mirrors normal last-one-wins annotation behavior.
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
            return MountPath ?? throw new InvalidOperationException(
                $"Volume '{VolumeName}' on resource '{context.Resource.Name}' does not declare a mount path.");
        }

        // The resolver can live on a different annotation than the one declaring the mount. The name-match
        // composition spells the mount and the compute environment binding as two separate calls, so scan
        // every binding for this volume rather than only consulting this one.
        var resolver = context.Resource.Annotations
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

    private static string ThrowIfNullOrEmpty([NotNull] string? argument, [CallerArgumentExpression(nameof(argument))] string? paramName = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(argument, paramName);
        return argument;
    }
}
