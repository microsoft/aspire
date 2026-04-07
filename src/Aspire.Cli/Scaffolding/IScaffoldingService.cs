// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Projects;

namespace Aspire.Cli.Scaffolding;

/// <summary>
/// Context for scaffolding a new AppHost project.
/// </summary>
/// <param name="Language">The language to scaffold.</param>
/// <param name="TargetDirectory">The directory to scaffold into.</param>
/// <param name="ProjectName">Optional project name.</param>
internal record ScaffoldContext(
    LanguageInfo Language,
    DirectoryInfo TargetDirectory,
    string? ProjectName = null);

/// <summary>
/// Service for scaffolding new AppHost projects.
/// </summary>
internal interface IScaffoldingService
{
    /// <summary>
    /// Scaffolds a new AppHost project.
    /// </summary>
    /// <param name="context">The scaffolding context.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns><see langword="true"/> when scaffolding succeeds; otherwise, <see langword="false"/>.</returns>
    Task<bool> ScaffoldAsync(ScaffoldContext context, CancellationToken cancellationToken);
}
