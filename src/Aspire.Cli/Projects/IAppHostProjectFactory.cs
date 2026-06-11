// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Cli.Projects;

/// <summary>
/// Factory for creating AppHost projects from resolved language information.
/// </summary>
internal interface IAppHostProjectFactory
{
    /// <summary>
    /// Creates an AppHost project handler for the specified language.
    /// </summary>
    /// <param name="language">The resolved language information.</param>
    /// <returns>A project handler for the language.</returns>
    IAppHostProject GetProject(LanguageInfo language);

    /// <summary>
    /// Tries to get a project handler for the specified AppHost file.
    /// Resolves the language from the file and creates the appropriate project.
    /// </summary>
    /// <param name="appHostFile">The AppHost file.</param>
    /// <returns>An appropriate project handler, or null if the file is not recognized.</returns>
    IAppHostProject? TryGetProject(FileInfo appHostFile);

    /// <summary>
    /// Gets a project handler for the specified AppHost file.
    /// Resolves the language from the file and creates the appropriate project.
    /// </summary>
    /// <param name="appHostFile">The AppHost file.</param>
    /// <returns>An appropriate project handler for the file.</returns>
    /// <exception cref="NotSupportedException">Thrown if no handler is available for the specified file.</exception>
    IAppHostProject GetProject(FileInfo appHostFile);
}
