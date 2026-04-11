// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Cli.Agents.Playwright;

/// <summary>
/// Interface for running playwright-cli commands.
/// </summary>
internal interface IPlaywrightCliRunner
{
    /// <summary>
    /// Gets the version of the playwright-cli if it is installed.
    /// </summary>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The version of the playwright-cli, or null if it is not installed.</returns>
    Task<SemVersion?> GetVersionAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Installs Playwright CLI skill files into the workspace.
    /// </summary>
    /// <param name="workingDirectory">The directory to run the command from (skill files are written relative to this).</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>True if skill installation succeeded, false otherwise.</returns>
    Task<bool> InstallSkillsAsync(string workingDirectory, CancellationToken cancellationToken);
}
