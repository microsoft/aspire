// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;
using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Pipelines;

/// <summary>
/// Service for managing pipeline output directories.
/// </summary>
[Experimental("ASPIREPIPELINES004", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
public interface IPipelineOutputService
{
    /// <summary>
    /// Gets the output directory for deployment artifacts.
    /// If no output path is configured, defaults to the AppHost directory's <c>aspire-output</c> folder.
    /// Falls back to the current directory when the AppHost directory is unavailable.
    /// </summary>
    /// <returns>The path to the output directory for deployment artifacts.</returns>
    string GetOutputDirectory();

    /// <summary>
    /// Gets the output directory for a specific resource's deployment artifacts.
    /// </summary>
    /// <param name="resource">The resource to get the output directory for.</param>
    /// <returns>The path to the output directory for the resource's deployment artifacts.</returns>
    string GetOutputDirectory(IResource resource);

    /// <summary>
    /// Gets a temporary directory for build artifacts.
    /// </summary>
    /// <returns>The path to a temporary directory for build artifacts.</returns>
    string GetTempDirectory();

    /// <summary>
    /// Gets a temporary directory for a specific resource's build artifacts.
    /// </summary>
    /// <param name="resource">The resource to get the temporary directory for.</param>
    /// <returns>The path to a temporary directory for the resource's build artifacts.</returns>
    string GetTempDirectory(IResource resource);
}
