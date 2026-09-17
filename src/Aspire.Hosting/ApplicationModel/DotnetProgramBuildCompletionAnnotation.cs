// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// Provides an optional successful-build barrier for consumers of a .NET program's build output.
/// </summary>
/// <remarks>
/// Consumers invoke the callback before using build output, without waiting for the application's runtime
/// dependencies or readiness. The build owner must support calls before its build plan is finalized,
/// propagate build failures and cancellation, and observe the current build attempt on each invocation.
/// A missing annotation means that the resource does not provide coordinated build readiness.
/// </remarks>
[Experimental("ASPIREPROJECTS001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
public sealed class DotnetProgramBuildCompletionAnnotation : IResourceAnnotation
{
    /// <summary>
    /// Initializes a new instance of the <see cref="DotnetProgramBuildCompletionAnnotation"/> class.
    /// </summary>
    /// <param name="callback">The callback that waits for successful build completion using the application services and caller's cancellation token.</param>
    /// <exception cref="ArgumentNullException"><paramref name="callback"/> is <see langword="null"/>.</exception>
    public DotnetProgramBuildCompletionAnnotation(Func<IServiceProvider, CancellationToken, Task> callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        Callback = callback;
    }

    /// <summary>
    /// Gets the callback that waits for the current build to complete successfully.
    /// </summary>
    /// <remarks>
    /// Invoke this callback for each operation; do not cache its returned task across retries or rebuilds.
    /// Cancellation of a consumer must not cancel the build or other consumers.
    /// </remarks>
    public Func<IServiceProvider, CancellationToken, Task> Callback { get; }
}
