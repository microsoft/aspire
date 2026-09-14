// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Cli.Agents;

/// <summary>
/// Resolves a catalog's available assets and installation targets.
/// </summary>
internal interface IAgentAssetCatalog
{
    string Name { get; }

    IReadOnlyList<AgentClientKind> SupportedClients { get; }

    IReadOnlyList<AgentAssetLocation> Locations { get; }

    Task<AgentAssetCatalogResult> ResolveAsync(string? requestedAssets, CancellationToken cancellationToken);

    /// <summary>
    /// Resolves a selected location's targets on enumeration, in workspace-then-user order.
    /// </summary>
    IEnumerable<AgentAssetInstallTarget> ResolveInstallTargets(
        AgentAssetLocation location,
        DirectoryInfo workspaceDirectory,
        DirectoryInfo homeDirectory,
        IEnvironment environment);

    /// <summary>
    /// Installs a file-backed asset, returning whether any files were updated or removed.
    /// </summary>
    Task<bool> InstallAsync(AgentAssetInstallTarget target, AgentAssetDefinition asset, CancellationToken cancellationToken);
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
