// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Cli.Agents;

/// <summary>
/// Creates installation targets using common workspace and home-relative path conventions.
/// </summary>
internal static class AgentAssetPathHelper
{
    /// <summary>
    /// Creates standard workspace- and home-relative targets in workspace-then-user order.
    /// </summary>
    internal static IEnumerable<AgentAssetInstallTarget> CreateDefaultTargets(
        AgentAssetLocation location,
        DirectoryInfo workspaceDirectory,
        DirectoryInfo homeDirectory)
    {
        var displayDirectory = location.RelativeAssetDirectory
            .Replace(Path.DirectorySeparatorChar, '/')
            .Replace(Path.AltDirectorySeparatorChar, '/');

        if (location.Scopes.HasFlag(AgentAssetLocationScope.Workspace))
        {
            yield return new(workspaceDirectory, location.RelativeAssetDirectory, displayDirectory);
        }

        if (location.Scopes.HasFlag(AgentAssetLocationScope.User))
        {
            yield return new(homeDirectory, location.RelativeAssetDirectory, $"~/{displayDirectory}");
        }
    }
}
