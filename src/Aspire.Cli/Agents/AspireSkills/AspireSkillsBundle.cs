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
        if (assets.Any(asset => asset.Definition.AssetKind != assetKind))
        {
            throw new ArgumentException("All Aspire-skills bundle assets must match the bundle's asset kind.", nameof(assets));
        }

        _version = version;
        _assetKind = assetKind;
        _assets = assets
            .Select(static asset => new ValidatedAspireSkillsBundleAsset(asset.Definition, [.. asset.Files]))
            .ToList();
    }

    /// <summary>
    /// Gets the bundle version.
    /// </summary>
    public string Version => _version;

    /// <summary>
    /// Gets the asset kind represented by this bundle.
    /// </summary>
    public AgentAssetKind AssetKind => _assetKind;

    /// <summary>
    /// Gets installable files for a definition supplied by this bundle.
    /// </summary>
    public Task<IReadOnlyList<AgentAssetFile>> GetAssetFilesAsync(
        AgentAssetDefinition asset,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(asset);
        cancellationToken.ThrowIfCancellationRequested();

        if (asset.AssetKind != _assetKind)
        {
            throw new InvalidOperationException(string.Format(
                CultureInfo.InvariantCulture,
                "Aspire-skills bundle contains '{0}' assets, but asset '{1}' has kind '{2}'.",
                _assetKind,
                asset.Name,
                asset.AssetKind));
        }

        // Definitions are the handles for bundle-owned payloads. Requiring the exact definition
        // returned by this bundle prevents a same-name asset from another bundle or source from
        // resolving to these files.
        var bundledAsset = _assets.FirstOrDefault(candidate => ReferenceEquals(candidate.Definition, asset));
        if (bundledAsset is null)
        {
            throw new InvalidOperationException(string.Format(
                CultureInfo.InvariantCulture,
                "Aspire-skills bundle does not own asset definition '{0}'.",
                asset.Name));
        }

        var files = bundledAsset.Files
            .Where(file => asset.ShouldInstallFile(file.RelativePath))
            .OrderBy(file => file.RelativePath, StringComparer.Ordinal)
            .ToList();

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
