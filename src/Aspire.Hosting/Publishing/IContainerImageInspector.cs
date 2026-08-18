// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;

namespace Aspire.Hosting.Publishing;

/// <summary>
/// Provides container image inspection capabilities for a container runtime.
/// </summary>
[Experimental("ASPIRECONTAINERRUNTIME001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
public interface IContainerImageInspector
{
    /// <summary>
    /// Inspects a container image and returns the runtime-specific image configuration as JSON.
    /// </summary>
    /// <param name="imageName">The image name or reference to inspect.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The image configuration JSON.</returns>
    Task<string> InspectImageConfigAsync(string imageName, CancellationToken cancellationToken);

    /// <summary>
    /// Inspects a container image manifest and returns the runtime-specific manifest as JSON.
    /// </summary>
    /// <param name="imageName">The image name or reference to inspect.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The image manifest JSON.</returns>
    Task<string> InspectImageManifestAsync(string imageName, CancellationToken cancellationToken);
}
