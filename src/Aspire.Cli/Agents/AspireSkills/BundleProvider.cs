// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Semver;

namespace Aspire.Cli.Agents.AspireSkills;

/// <summary>
/// Loads and validates bundles from the Aspire skills repository.
/// </summary>
internal interface IAspireSkillsBundleProvider
{
    Task<AspireSkillsBundle> LoadAsync(
        AgentAssetKind assetKind,
        DirectoryInfo bundleDirectory,
        string currentCliVersion,
        string currentSdkVersion,
        bool skipCompatibilityCheck,
        CancellationToken cancellationToken);
}

internal sealed class AspireSkillsBundleProvider : IAspireSkillsBundleProvider
{
    public async Task<AspireSkillsBundle> LoadAsync(
        AgentAssetKind assetKind,
        DirectoryInfo bundleDirectory,
        string currentCliVersion,
        string currentSdkVersion,
        bool skipCompatibilityCheck,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(bundleDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(currentCliVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(currentSdkVersion);

        var descriptor = BundleDescriptor.GetDescriptor(assetKind);
        var manifestPath = Path.Combine(bundleDirectory.FullName, descriptor.ManifestFileName);
        if (!File.Exists(manifestPath))
        {
            throw new InvalidOperationException(string.Format(CultureInfo.InvariantCulture, "Aspire skills bundle manifest was not found at '{0}'.", manifestPath));
        }

        await using var manifestStream = File.OpenRead(manifestPath);
        var manifestDocument = await JsonSerializer.DeserializeAsync(
            manifestStream,
            AspireSkillsJsonSerializerContext.Default.BundleManifestDocument,
            cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Aspire skills bundle manifest is empty or invalid.");
        var manifest = manifestDocument.ToManifest(descriptor.AssetsDirectoryName);
        ValidateManifest(bundleDirectory, descriptor, manifest, currentCliVersion, currentSdkVersion, skipCompatibilityCheck);

        return new AspireSkillsBundle(bundleDirectory, descriptor, manifest);
    }

    private static void ValidateManifest(
        DirectoryInfo bundleDirectory,
        BundleDescriptor descriptor,
        BundleManifest manifest,
        string currentCliVersion,
        string currentSdkVersion,
        bool skipCompatibilityCheck)
    {
        if (string.IsNullOrWhiteSpace(manifest.Version))
        {
            throw new InvalidOperationException("Aspire skills bundle manifest must specify a version.");
        }

        // Cached and embedded bundles were already selected by their bundle version. Their
        // supports range can lag a dogfood CLI version, so only fresh remote bundles are gated.
        if (!skipCompatibilityCheck)
        {
            ValidateCompatibility(manifest.Supports, currentCliVersion, currentSdkVersion);
        }

        if (manifest.Assets is not { Length: > 0 })
        {
            throw new InvalidOperationException(string.Format(CultureInfo.InvariantCulture, "Aspire skills bundle manifest must contain at least one {0}.", descriptor.DisplayName));
        }

        var assetNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var asset in manifest.Assets)
        {
            if (string.IsNullOrWhiteSpace(asset.Name))
            {
                throw new InvalidOperationException(string.Format(CultureInfo.InvariantCulture, "Aspire skills bundle manifest contains a {0} without a name.", descriptor.DisplayName));
            }

            if (!assetNames.Add(asset.Name))
            {
                throw new InvalidOperationException(string.Format(CultureInfo.InvariantCulture, "Aspire skills bundle manifest contains duplicate {0} '{1}'.", descriptor.DisplayName, asset.Name));
            }

            if (string.IsNullOrWhiteSpace(asset.Description))
            {
                throw new InvalidOperationException(string.Format(CultureInfo.InvariantCulture, "Aspire skills bundle {0} '{1}' must specify a description.", descriptor.DisplayName, asset.Name));
            }

            if (asset.Files is not { Length: > 0 })
            {
                throw new InvalidOperationException(string.Format(CultureInfo.InvariantCulture, "Aspire skills bundle {0} '{1}' does not contain any files.", descriptor.DisplayName, asset.Name));
            }

            foreach (var excludedPath in asset.InstallExcludedRelativePaths ?? [])
            {
                _ = AspireSkillsBundle.NormalizeRelativePath(excludedPath);
            }

            foreach (var file in asset.Files)
            {
                ValidateFile(bundleDirectory, descriptor, asset.Name, file);
            }

            descriptor.ValidateAsset(asset);
        }
    }

    private static void ValidateFile(DirectoryInfo bundleDirectory, BundleDescriptor descriptor, string assetName, BundleFile file)
    {
        var relativePath = AspireSkillsBundle.NormalizeRelativePath(file.RelativePath);
        if (string.IsNullOrWhiteSpace(file.Sha256))
        {
            throw new InvalidOperationException(string.Format(CultureInfo.InvariantCulture, "Aspire skills bundle file '{0}' in {1} '{2}' does not specify a SHA-256 hash.", relativePath, descriptor.DisplayName, assetName));
        }

        var fullPath = Path.Combine(bundleDirectory.FullName, descriptor.AssetsDirectoryName, assetName, relativePath);
        if (!File.Exists(fullPath))
        {
            throw new InvalidOperationException(string.Format(CultureInfo.InvariantCulture, "Aspire skills bundle file '{0}' in {1} '{2}' was not found.", relativePath, descriptor.DisplayName, assetName));
        }

        using var stream = File.OpenRead(fullPath);
        var actualHash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        var expectedHash = AspireSkillsBundle.NormalizeSha256(file.Sha256);
        if (!string.Equals(expectedHash, actualHash, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(string.Format(CultureInfo.InvariantCulture, "Aspire skills bundle file '{0}' in {1} '{2}' failed SHA-256 verification.", relativePath, descriptor.DisplayName, assetName));
        }

        descriptor.ValidateFile(assetName, relativePath, fullPath);
    }

    private static void ValidateCompatibility(BundleSupports? supports, string currentCliVersion, string currentSdkVersion)
    {
        if (supports is null)
        {
            throw new InvalidOperationException("Aspire skills bundle manifest must specify supported Aspire versions.");
        }

        if (string.IsNullOrWhiteSpace(supports.AspireCli))
        {
            throw new InvalidOperationException("Aspire skills bundle manifest must specify supports.aspireCli.");
        }

        if (!IsVersionInRange(currentCliVersion, supports.AspireCli))
        {
            throw new InvalidOperationException(string.Format(CultureInfo.InvariantCulture, "Aspire skills bundle supports Aspire CLI versions '{0}', but the current CLI version is '{1}'.", supports.AspireCli, currentCliVersion));
        }

        if (!string.IsNullOrWhiteSpace(supports.AspireSdk) && !IsVersionInRange(currentSdkVersion, supports.AspireSdk))
        {
            throw new InvalidOperationException(string.Format(CultureInfo.InvariantCulture, "Aspire skills bundle supports Aspire SDK versions '{0}', but the current SDK version is '{1}'.", supports.AspireSdk, currentSdkVersion));
        }
    }

    private static bool IsVersionInRange(string version, string range)
    {
        var normalizedVersion = ParseCompatibilityVersion(version);
        var comparators = range.Replace(',', ' ').Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (comparators.Length == 0)
        {
            throw new InvalidOperationException("Aspire skills bundle contains an empty version range.");
        }

        foreach (var comparator in comparators)
        {
            if (comparator is "*" or "x" or "X")
            {
                continue;
            }

            if (!SatisfiesComparator(normalizedVersion, comparator))
            {
                return false;
            }
        }

        return true;
    }

    private static bool SatisfiesComparator(SemVersion version, string comparator)
    {
        var (op, operandText) = ParseComparator(comparator);
        var comparison = SemVersion.ComparePrecedence(version, ParseCompatibilityVersion(operandText));
        return op switch
        {
            ">" => comparison > 0,
            ">=" => comparison >= 0,
            "<" => comparison < 0,
            "<=" => comparison <= 0,
            "=" or "==" => comparison == 0,
            _ => throw new InvalidOperationException(string.Format(CultureInfo.InvariantCulture, "Unsupported Aspire skills bundle version comparator '{0}'.", op))
        };
    }

    private static (string Operator, string Operand) ParseComparator(string comparator)
    {
        foreach (var op in new[] { ">=", "<=", "==", ">", "<", "=" })
        {
            if (comparator.StartsWith(op, StringComparison.Ordinal))
            {
                var operand = comparator[op.Length..];
                if (string.IsNullOrWhiteSpace(operand))
                {
                    throw new InvalidOperationException(string.Format(CultureInfo.InvariantCulture, "Aspire skills bundle contains an invalid version comparator '{0}'.", comparator));
                }

                return (op, operand);
            }
        }

        return ("=", comparator);
    }

    private static SemVersion ParseCompatibilityVersion(string version)
    {
        if (!SemVersion.TryParse(version, SemVersionStyles.Any, out var parsedVersion))
        {
            throw new InvalidOperationException(string.Format(CultureInfo.InvariantCulture, "Aspire skills bundle contains an invalid version value '{0}'.", version));
        }

        return SemVersion.Parse(string.Create(CultureInfo.InvariantCulture, $"{parsedVersion.Major}.{parsedVersion.Minor}.{parsedVersion.Patch}"), SemVersionStyles.Strict);
    }

}