// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Projects;

namespace Aspire.Cli.Agents;

/// <summary>
/// Composes registered asset catalogs and applies common filtering and ordering.
/// </summary>
internal sealed class AgentAssetCatalogProvider : IAgentAssetCatalogProvider
{
    private readonly Dictionary<AgentAssetKind, IAgentAssetCatalog> _catalogs = [];

    public AgentAssetCatalogProvider(IEnumerable<IAgentAssetCatalog> catalogs)
    {
        foreach (var catalog in catalogs)
        {
            if (!_catalogs.TryAdd(catalog.AssetKind, catalog))
            {
                throw new InvalidOperationException(
                    $"Multiple agent asset catalogs are registered for asset kind '{catalog.AssetKind}'.");
            }
        }
    }

    public IAgentAssetCatalog GetCatalog(AgentAssetKind assetKind)
        => _catalogs.TryGetValue(assetKind, out var catalog)
            ? catalog
            : throw new InvalidOperationException($"No agent asset catalog is registered for asset kind '{assetKind}'.");

    public async Task<AgentAssetCatalogResult> ResolveAsync(
        AgentAssetKind assetKind,
        string? requestedAssets,
        LanguageId? detectedLanguage,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var result = await GetCatalog(assetKind).ResolveAsync(requestedAssets, cancellationToken);
        // Keep prompts stable regardless of source order, using the same case-insensitive
        // names accepted by command-line selection.
        return result with
        {
            Assets = result.Assets
                .Where(asset => asset.IsApplicableToLanguage(detectedLanguage))
                .OrderBy(static asset => asset.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray()
        };
    }
}
