// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Projects;

namespace Aspire.Cli.Agents;

/// <summary>
/// Provides registered catalogs and their language-applicable assets to CLI commands.
/// </summary>
internal interface IAgentAssetCatalogProvider
{
    /// <summary>
    /// Gets a registered catalog without acquiring its assets or resolving installation paths.
    /// </summary>
    IAgentAssetCatalog GetCatalog(AgentAssetKind assetKind);

    Task<AgentAssetCatalogResult> ResolveAsync(
        AgentAssetKind assetKind,
        string? requestedAssets,
        LanguageId? detectedLanguage,
        CancellationToken cancellationToken);
}
