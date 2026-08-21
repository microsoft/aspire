// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Maui;

/// <summary>
/// Context passed to build-argument callbacks registered with
/// <see cref="MauiBuildArgumentsExtensions.WithMauiBuildArguments{T}(IResourceBuilder{T}, Func{MauiBuildArgumentsCallbackContext, System.Threading.Tasks.Task})"/>
/// and
/// <see cref="MauiBuildArgumentsExtensions.WithMauiLaunchArguments{T}(IResourceBuilder{T}, Func{MauiBuildArgumentsCallbackContext, System.Threading.Tasks.Task})"/>.
/// </summary>
/// <remarks>
/// Mutate <see cref="Arguments"/> in place to add, remove, or replace the arguments that will be
/// passed to <c>dotnet</c> for the <see cref="Step"/> this callback is registered for.
/// </remarks>
[AspireExport(ExposeProperties = true)]
public sealed class MauiBuildArgumentsCallbackContext
{
    internal MauiBuildArgumentsCallbackContext(
        MauiBuildStep step,
        IList<string> arguments,
        IResource resource,
        CancellationToken cancellationToken)
    {
        Step = step;
        Arguments = arguments;
        Resource = resource;
        CancellationToken = cancellationToken;
    }

    /// <summary>
    /// Gets the build step this callback is participating in.
    /// </summary>
    public MauiBuildStep Step { get; }

    /// <summary>
    /// Gets the mutable list of arguments passed to <c>dotnet</c> for the current <see cref="Step"/>.
    /// Add, remove, or replace entries to influence the command that is executed.
    /// </summary>
    public IList<string> Arguments { get; }

    /// <summary>
    /// Gets the MAUI platform resource the arguments apply to.
    /// </summary>
    public IResource Resource { get; }

    /// <summary>
    /// Gets a token that is cancelled if the resource start is cancelled.
    /// </summary>
    public CancellationToken CancellationToken { get; }
}
