// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Cli.Projects;

/// <summary>
/// Service for managing AppHost language selection and persistence.
/// </summary>
internal interface ILanguageService
{
    /// <summary>
    /// Gets the IAppHostProject matching the saved language config, or null if not configured.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The configured project handler, or null if not set.</returns>
    Task<IAppHostProject?> GetConfiguredProjectAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Saves the project's language ID to configuration.
    /// </summary>
    /// <param name="project">The project handler to save.</param>
    /// <param name="isGlobal">Whether to save to global settings.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task SetLanguageAsync(IAppHostProject project, bool isGlobal = false, CancellationToken cancellationToken = default);

    /// <summary>
    /// Prompts the user to select from registered IAppHostProject implementations.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The selected project handler.</returns>
    Task<IAppHostProject> PromptForProjectAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the configured project or prompts, validating explicit language ID.
    /// </summary>
    /// <param name="explicitLanguageId">An explicitly specified language ID (e.g., from command line).</param>
    /// <param name="saveLanguageSelection">Whether to save the language selection to config if prompted.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The project handler to use.</returns>
    Task<IAppHostProject> GetOrPromptForProjectAsync(string? explicitLanguageId = null, bool saveLanguageSelection = true, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the configured project or prompts, validating explicit language ID, and returns whether the prompted selection still needs to be persisted.
    /// </summary>
    /// <param name="explicitLanguageId">An explicitly specified language ID (e.g., from command line).</param>
    /// <param name="saveLanguageSelection">Whether to save the language selection to config if prompted.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The project handler to use and whether the selection should be saved after the caller completes its work.</returns>
    Task<AppHostProjectSelection> GetOrPromptForProjectSelectionAsync(string? explicitLanguageId = null, bool saveLanguageSelection = true, CancellationToken cancellationToken = default);
}

/// <summary>
/// Represents the selected AppHost project and whether that selection still needs to be persisted.
/// </summary>
internal sealed record AppHostProjectSelection(IAppHostProject Project, bool ShouldPersistSelection);
