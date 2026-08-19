// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;

namespace Aspire.Cli.Agents.AspireSkills;

/// <summary>
/// A validated Aspire-skills bundle.
/// </summary>
internal sealed class AspireSkillsBundle
{
    private readonly string _version;
    private readonly AgentAssetKind _assetKind;
    private readonly IReadOnlyList<ValidatedAspireSkillsBundleAsset> _assets;

    internal AspireSkillsBundle(string version, AgentAssetKind assetKind, IReadOnlyList<ValidatedAspireSkillsBundleAsset> assets)
    {
        _version = version;
        _assetKind = assetKind;
        _assets = assets;
    }

    /// <summary>
    /// Gets the bundle version.
    /// </summary>
    public string Version => _version;

    /// <summary>
    /// Gets installable files for the specified asset.
    /// </summary>
    public Task<IReadOnlyList<AgentAssetFile>> GetAssetFilesAsync(
        AgentAssetDefinition asset,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(asset);
        cancellationToken.ThrowIfCancellationRequested();

        var bundledAsset = _assets.FirstOrDefault(a => string.Equals(a.Definition.Name, asset.Name, StringComparison.Ordinal));
        if (bundledAsset is null)
        {
            throw new InvalidOperationException(string.Format(CultureInfo.InvariantCulture, "Aspire-skills bundle does not contain asset '{0}'.", asset.Name));
        }

        List<AgentAssetFile> files = [];
        foreach (var bundledFile in bundledAsset.Files.OrderBy(f => f.RelativePath, StringComparer.Ordinal))
        {
            var relativePath = bundledFile.RelativePath;
            if (!asset.ShouldInstallFile(relativePath) ||
                !bundledAsset.Definition.ShouldInstallFile(relativePath))
            {
                continue;
            }

            files.Add(bundledFile);
        }

        return Task.FromResult<IReadOnlyList<AgentAssetFile>>(files);
    }

    /// <summary>
    /// Gets the installable asset definitions declared by the bundle manifest.
    /// </summary>
    public IReadOnlyList<AgentAssetDefinition> GetAssetDefinitions()
    {
        return _assets
            .Select(static asset => asset.Definition)
            .ToList();
    }
}

internal sealed record ValidatedAspireSkillsBundleAsset(
    AgentAssetDefinition Definition,
    IReadOnlyList<AgentAssetFile> Files);
