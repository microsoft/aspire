// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;

namespace Aspire.Cli.Agents.AspireSkills;

/// <summary>
/// Represents a validated bundle from aspire-skills.
/// </summary>
internal sealed class AspireSkillsBundle
{
    private readonly DirectoryInfo _bundleDirectory;
    private readonly SkillBundleManifest _manifest;
    private readonly AgentAssetKind _assetKind;
    private readonly string _relativeDirectoryName;

    internal AspireSkillsBundle(
        DirectoryInfo bundleDirectory,
        SkillBundleManifest manifest,
        AgentAssetKind assetKind,
        string relativeDirectoryName)
    {
        _bundleDirectory = bundleDirectory;
        _manifest = manifest;
        _assetKind = assetKind;
        _relativeDirectoryName = relativeDirectoryName;
    }

    /// <summary>
    /// Gets the bundle version from the manifest.
    /// </summary>
    public string Version => _manifest.Version ?? string.Empty;

    /// <summary>
    /// Gets the type of assets contained in this bundle.
    /// </summary>
    public AgentAssetKind AssetType => _assetKind;

    /// <summary>
    /// Gets installable files for the specified agent asset.
    /// </summary>
    public async Task<IReadOnlyList<AgentAssetFile>> GetAgentAssetFilesAsync(AgentAssetDefinition asset, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(asset);

        var manifestAsset = _manifest.Assets.FirstOrDefault(candidate => string.Equals(candidate.Name, asset.Name, StringComparison.Ordinal));
        if (manifestAsset is null)
        {
            throw new InvalidOperationException(string.Format(CultureInfo.InvariantCulture, "Aspire skills bundle does not contain asset '{0}'.", asset.Name));
        }

        List<AgentAssetFile> files = [];
        foreach (var manifestFile in manifestAsset.Files.OrderBy(file => file.RelativePath, StringComparer.Ordinal))
        {
            var relativePath = manifestFile.RelativePath!;
            if (!asset.ShouldInstallFile(relativePath) || !ShouldInstallFile(manifestAsset, relativePath))
            {
                continue;
            }

            var fullPath = Path.Combine(_bundleDirectory.FullName, _relativeDirectoryName, asset.Name, relativePath);
            files.Add(new AgentAssetFile(relativePath, await File.ReadAllTextAsync(fullPath, cancellationToken).ConfigureAwait(false)));
        }

        return files;
    }

    /// <summary>
    /// Gets the installable agent asset definitions declared by the bundle manifest.
    /// </summary>
    public IReadOnlyList<AgentAssetDefinition> GetAgentAssetDefinitions()
    {
        return _manifest.Assets
            .Select(asset => AgentAssetDefinition.CreateAspireSkillsBundle(
                asset.Name!,
                asset.Description!,
                AssetType,
                asset.InstallExcludedRelativePaths,
                asset.ApplicableLanguages))
            .ToList();
    }

    private static bool ShouldInstallFile(SkillBundleAsset asset, string relativePath)
    {
        foreach (var excludedPath in asset.InstallExcludedRelativePaths ?? [])
        {
            if (PathMatchesOrIsUnder(relativePath, excludedPath))
            {
                return false;
            }
        }

        return true;
    }

    private static bool PathMatchesOrIsUnder(string relativePath, string excludedPath)
    {
        if (string.Equals(relativePath, excludedPath, StringComparison.Ordinal))
        {
            return true;
        }

        if (!relativePath.StartsWith(excludedPath, StringComparison.Ordinal))
        {
            return false;
        }

        return relativePath.Length > excludedPath.Length && relativePath[excludedPath.Length] == Path.DirectorySeparatorChar;
    }
}
