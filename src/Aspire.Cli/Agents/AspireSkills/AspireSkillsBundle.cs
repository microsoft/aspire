// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Cli.Agents.AspireSkills;

/// <summary>
/// A validated bundle of agent skills or extensions.
/// </summary>
internal sealed class AspireSkillsBundle
{
    private readonly IReadOnlyList<ValidatedAspireSkillsBundleAsset> _assets;

    internal AspireSkillsBundle(
        string version,
        AgentAssetKind assetKind,
        IReadOnlyList<ValidatedAspireSkillsBundleAsset> assets)
    {
        if (assets.Any(asset => asset.Definition.AssetKind != assetKind))
        {
            throw new ArgumentException("All bundle assets must have the bundle's asset kind.", nameof(assets));
        }

        Version = version;
        AssetKind = assetKind;
        _assets = [.. assets];
        Assets = _assets.Select(static asset => asset.Definition).ToArray();
    }

    public string Version { get; }

    public AgentAssetKind AssetKind { get; }

    /// <summary>
    /// Gets the installable definitions declared by the bundle manifest.
    /// </summary>
    public IReadOnlyList<AgentAssetDefinition> Assets { get; }

    /// <summary>
    /// Gets validated files for an asset, applying its install exclusions.
    /// </summary>
    public Task<IReadOnlyList<AgentAssetFile>> GetAssetFilesAsync(
        AgentAssetDefinition asset,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(asset);
        cancellationToken.ThrowIfCancellationRequested();

        var bundledAsset = _assets.FirstOrDefault(candidate =>
            candidate.Definition.AssetKind == asset.AssetKind &&
            string.Equals(candidate.Definition.Name, asset.Name, StringComparison.Ordinal));
        if (bundledAsset is null)
        {
            throw new InvalidOperationException($"Aspire bundle does not contain {asset.AssetKind} '{asset.Name}'.");
        }

        IReadOnlyList<AgentAssetFile> files = bundledAsset.Files
            .Where(file => asset.ShouldInstallFile(file.RelativePath) &&
                bundledAsset.Definition.ShouldInstallFile(file.RelativePath))
            .OrderBy(static file => file.RelativePath, StringComparer.Ordinal)
            .ToArray();

        return Task.FromResult(files);
    }
}

internal sealed record ValidatedAspireSkillsBundleAsset(
    AgentAssetDefinition Definition,
    IReadOnlyList<AgentAssetFile> Files);
