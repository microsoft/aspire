// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;

namespace Aspire.Cli.Agents.AspireSkills;

/// <summary>
/// Represents a validated agent asset bundle from the Aspire skills repository.
/// </summary>
internal sealed class AspireSkillsBundle
{
    private readonly DirectoryInfo _bundleDirectory;
    private readonly BundleDescriptor _descriptor;
    private readonly BundleManifest _manifest;

    internal AspireSkillsBundle(
        DirectoryInfo bundleDirectory,
        BundleDescriptor descriptor,
        BundleManifest manifest)
    {
        _bundleDirectory = bundleDirectory;
        _descriptor = descriptor;
        _manifest = manifest;
    }

    /// <summary>
    /// Gets the bundle version from the manifest.
    /// </summary>
    public string Version => _manifest.Version!;

    /// <summary>
    /// Gets the kind of assets contained in the bundle.
    /// </summary>
    public AgentAssetKind AssetKind => _descriptor.AssetKind;

    /// <summary>
    /// Gets installable files for the specified agent asset.
    /// </summary>
    public async Task<IReadOnlyList<AgentAssetFile>> GetAgentAssetFilesAsync(AgentAssetDefinition asset, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(asset);

        if (asset.AssetKind != AssetKind)
        {
            throw new InvalidOperationException(string.Format(CultureInfo.InvariantCulture, "Aspire skills bundle contains {0} assets, not {1} assets.", AssetKind, asset.AssetKind));
        }

        var manifestAsset = _manifest.Assets.FirstOrDefault(candidate => string.Equals(candidate.Name, asset.Name, StringComparison.Ordinal));
        if (manifestAsset is null)
        {
            throw new InvalidOperationException(string.Format(CultureInfo.InvariantCulture, "Aspire skills bundle does not contain asset '{0}'.", asset.Name));
        }

        List<AgentAssetFile> files = [];
        var manifestFiles = manifestAsset.Files
            ?? throw new InvalidOperationException(string.Format(CultureInfo.InvariantCulture, "Aspire skills bundle asset '{0}' does not contain any files.", asset.Name));
        foreach (var manifestFile in manifestFiles.OrderBy(f => f.RelativePath, StringComparer.Ordinal))
        {
            var relativePath = NormalizeRelativePath(manifestFile.RelativePath!);
            if (!asset.ShouldInstallFile(relativePath))
            {
                continue;
            }

            var fullPath = Path.Combine(_bundleDirectory.FullName, _descriptor.AssetsDirectoryName, asset.Name, relativePath);
            files.Add(new AgentAssetFile(relativePath, await File.ReadAllTextAsync(fullPath, cancellationToken).ConfigureAwait(false)));
        }

        return files;
    }

    /// <summary>
    /// Gets the installable asset definitions declared by the bundle manifest.
    /// </summary>
    public IReadOnlyList<AgentAssetDefinition> GetAgentAssetDefinitions()
    {
        return _manifest.Assets
            .Select(asset => AgentAssetDefinition.CreateAspireSkillsBundle(
                asset.Name!,
                asset.Description!,
                AssetKind,
                (asset.InstallExcludedRelativePaths ?? []).Select(NormalizeRelativePath).ToArray(),
                asset.ApplicableLanguages ?? []))
            .ToList();
    }

    internal static string NormalizeRelativePath(string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            throw new InvalidOperationException("Aspire skills bundle contains an empty relative path.");
        }

        var normalizedPath = relativePath
            .Replace('/', Path.DirectorySeparatorChar)
            .Replace('\\', Path.DirectorySeparatorChar);

        if (Path.IsPathRooted(normalizedPath))
        {
            throw new InvalidOperationException(string.Format(CultureInfo.InvariantCulture, "Aspire skills bundle path '{0}' must be relative.", relativePath));
        }

        var segments = normalizedPath.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0 || segments.Any(segment => segment is "." or ".."))
        {
            throw new InvalidOperationException(string.Format(CultureInfo.InvariantCulture, "Aspire skills bundle path '{0}' is not safe.", relativePath));
        }

        return Path.Combine(segments);
    }

    internal static string NormalizeSha256(string sha256)
    {
        const string prefix = "sha256-";
        return sha256.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? sha256[prefix.Length..]
            : sha256;
    }
}
