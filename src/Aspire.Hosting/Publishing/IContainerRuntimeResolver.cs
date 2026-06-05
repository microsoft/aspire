// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;

namespace Aspire.Hosting.Publishing;

/// <summary>
/// Resolves the configured or auto-detected container runtime asynchronously.
/// The result is cached after the first resolution.
/// </summary>
[Experimental("ASPIRECONTAINERRUNTIME001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
public interface IContainerRuntimeResolver
{
    /// <summary>
    /// Resolves the container runtime, detecting it from the environment if not explicitly configured.
    /// The result is cached after the first call.
    /// </summary>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The resolved container runtime.</returns>
    Task<IContainerRuntime> ResolveAsync(CancellationToken cancellationToken = default);
}
