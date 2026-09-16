// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Projects;

namespace Aspire.Cli.Agents;

/// <summary>
/// Composes registered asset catalogs and applies common filtering and ordering.
/// </summary>
internal sealed class AgentAssetCatalogProvider : IAgentAssetCatalogProvider
{
    private readonly IEnumerable<IAgentAssetCatalog> _catalogs;

    public AgentAssetCatalogProvider(IEnumerable<IAgentAssetCatalog> catalogs)
    {
        _catalogs = catalogs;
    }

    public IEnumerable<IAgentAssetCatalog> GetCatalogs() => _catalogs;

    public async Task<AgentAssetCatalogResult> ResolveAsync(
        IAgentAssetCatalog catalog,
        string? requestedAssets,
        LanguageId? detectedLanguage,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var result = await catalog.ResolveAsync(requestedAssets, cancellationToken);
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
