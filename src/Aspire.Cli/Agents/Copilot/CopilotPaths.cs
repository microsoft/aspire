// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Cli.Agents.Copilot;

/// <summary>
/// Resolves user-level GitHub Copilot configuration paths.
/// </summary>
internal static class CopilotPaths
{
    private const string CopilotHomeEnvironmentVariable = "COPILOT_HOME";
    private const string DefaultCopilotDirectoryName = ".copilot";

    /// <summary>
    /// Gets the user-level GitHub Copilot configuration directory.
    /// </summary>
    /// <param name="homeDirectory">The user's home directory.</param>
    /// <param name="environment">The environment abstraction used to resolve <c>COPILOT_HOME</c>.</param>
    /// <returns>The configured Copilot home, or <c>~/.copilot</c> when no override is set.</returns>
    public static string GetConfigDirectory(DirectoryInfo homeDirectory, IEnvironment environment)
    {
        var configuredHome = environment.GetEnvironmentVariable(CopilotHomeEnvironmentVariable);
        return !string.IsNullOrEmpty(configuredHome)
            ? configuredHome
            : Path.Combine(homeDirectory.FullName, DefaultCopilotDirectoryName);
    }
}
