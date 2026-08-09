// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;

namespace Aspire.Hosting.Pipelines;

/// <summary>
/// Provides deployment state management functionality.
/// </summary>
[Experimental("ASPIREPIPELINES002", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
public interface IDeploymentStateManager
{
    /// <summary>
    /// Gets the file path where deployment state is stored, if applicable.
    /// </summary>
    string? StateFilePath { get; }

    /// <summary>
    /// Acquires a specific section of the deployment state with version tracking for concurrency control.
    /// The returned section is an immutable snapshot of the data at the time of acquisition.
    /// </summary>
    /// <param name="sectionName">The name of the section to acquire (e.g., "Parameters", "Azure").</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A StateSection containing the section data and version information.</returns>
    Task<DeploymentStateSection> AcquireSectionAsync(string sectionName, CancellationToken cancellationToken = default);

    /// <summary>
    /// Saves a section of deployment state with optimistic concurrency control.
    /// The section must have a matching version number or a concurrency exception will be thrown.
    /// </summary>
    /// <param name="section">The section to save, including version information.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <exception cref="InvalidOperationException">Thrown when a version conflict is detected, indicating the section was modified after it was acquired.</exception>
    Task SaveSectionAsync(DeploymentStateSection section, CancellationToken cancellationToken = default);

    /// <summary>
    /// Delete a section of deployment state with optimistic concurrency control.
    /// The section must have a matching version number or a concurrency exception will be thrown.
    /// </summary>
    /// <param name="section">The section to delete, including version information.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <exception cref="InvalidOperationException">Thrown when a version conflict is detected, indicating the section was modified after it was acquired.</exception>
    Task DeleteSectionAsync(DeploymentStateSection section, CancellationToken cancellationToken = default);

    /// <summary>
    /// Clears all deployment state, removing all sections and the underlying storage.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    Task ClearAllStateAsync(CancellationToken cancellationToken = default);
}
