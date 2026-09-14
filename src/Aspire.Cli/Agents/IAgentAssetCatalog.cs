// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Cli.Agents;

/// <summary>
/// Resolves a catalog's available assets and installation targets.
/// </summary>
internal interface IAgentAssetCatalog
{
    string Name { get; }

    IReadOnlyList<AgentClient> SupportedClients { get; }

    IReadOnlyList<AgentAssetLocation> Locations { get; }

    AgentAssetFileInstaller FileInstaller { get; }

    Task<AgentAssetCatalogResult> ResolveAsync(string? requestedAssets, CancellationToken cancellationToken);

    /// <summary>
    /// Resolves a selected location's targets on enumeration, in workspace-then-user order.
    /// </summary>
    IEnumerable<AgentAssetInstallTarget> ResolveInstallTargets(
        AgentAssetLocation location,
        DirectoryInfo workspaceDirectory,
        DirectoryInfo homeDirectory,
        IEnvironment environment);
}

/// <summary>
/// Describes the resolved assets and any diagnostic that should precede selection.
/// </summary>
internal sealed record AgentAssetCatalogResult(
    IReadOnlyList<AgentAssetDefinition> Assets,
    string? DiagnosticMessage,
    bool IsFailure);

/// <summary>
/// Represents a resolved root and relative directory for an agent asset installation.
/// </summary>
internal readonly record struct AgentAssetInstallTarget(
    DirectoryInfo RootDirectory,
    string RelativeAssetDirectory,
    string DisplayDirectory);
