// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using System.Security.Cryptography;

namespace Aspire.Cli.Agents.AspireSkills;

/// <summary>
/// Represents a validated Aspire skills bundle on disk.
/// </summary>
internal sealed class AspireSkillsBundle
{
    private const string ManifestFileName = "skill-manifest.json";
    private const string SkillsDirectoryName = "skills";

    private readonly DirectoryInfo _bundleDirectory;
    private readonly SkillBundleManifest _manifest;

    private AspireSkillsBundle(DirectoryInfo bundleDirectory, SkillBundleManifest manifest)
    {
        _bundleDirectory = bundleDirectory;
        _manifest = manifest;
    }

    /// <summary>
    /// Gets the bundle version from the manifest.
    /// </summary>
    public string Version => _manifest.Version ?? string.Empty;

    /// <summary>
    /// Loads and validates a bundle from disk.
    /// </summary>
    public static async Task<AspireSkillsBundle> LoadAsync(DirectoryInfo bundleDirectory, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(bundleDirectory);

        var manifestPath = Path.Combine(bundleDirectory.FullName, ManifestFileName);
        if (!File.Exists(manifestPath))
        {
            throw new InvalidOperationException(string.Format(CultureInfo.InvariantCulture, "Aspire skills bundle manifest was not found at '{0}'.", manifestPath));
        }

        await using var manifestStream = File.OpenRead(manifestPath);
        var manifest = await System.Text.Json.JsonSerializer.DeserializeAsync(
            manifestStream,
            AspireSkillsJsonSerializerContext.Default.SkillBundleManifest,
            cancellationToken).ConfigureAwait(false);

        if (manifest is null)
        {
            throw new InvalidOperationException("Aspire skills bundle manifest is empty or invalid.");
        }

        ValidateManifest(bundleDirectory, manifest);

        return new AspireSkillsBundle(bundleDirectory, manifest);
    }

    /// <summary>
    /// Gets installable files for the specified skill.
    /// </summary>
    public async Task<IReadOnlyList<SkillAssetFile>> GetSkillFilesAsync(SkillDefinition skill, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(skill);

        var manifestSkill = _manifest.Skills.FirstOrDefault(s => string.Equals(s.Name, skill.Name, StringComparison.Ordinal));
        if (manifestSkill is null)
        {
            throw new InvalidOperationException(string.Format(CultureInfo.InvariantCulture, "Aspire skills bundle does not contain skill '{0}'.", skill.Name));
        }

        List<SkillAssetFile> files = [];
        var manifestFiles = manifestSkill.Files
            ?? throw new InvalidOperationException(string.Format(CultureInfo.InvariantCulture, "Aspire skills bundle skill '{0}' does not contain any files.", skill.Name));
        foreach (var manifestFile in manifestFiles.OrderBy(f => f.RelativePath, StringComparer.Ordinal))
        {
            var relativePath = NormalizeRelativePath(manifestFile.RelativePath!);
            if (!skill.ShouldInstallFile(relativePath) || !ShouldInstallFile(manifestSkill, relativePath))
            {
                continue;
            }

            var fullPath = Path.Combine(_bundleDirectory.FullName, SkillsDirectoryName, skill.Name, relativePath);
            files.Add(new SkillAssetFile(relativePath, await File.ReadAllTextAsync(fullPath, cancellationToken).ConfigureAwait(false)));
        }

        return files;
    }

    private static void ValidateManifest(DirectoryInfo bundleDirectory, SkillBundleManifest manifest)
    {
        if (string.IsNullOrWhiteSpace(manifest.Version))
        {
            throw new InvalidOperationException("Aspire skills bundle manifest must specify a version.");
        }

        var skills = manifest.Skills;
        if (skills is not { Length: > 0 })
        {
            throw new InvalidOperationException("Aspire skills bundle manifest must contain at least one skill.");
        }

        var skillNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var skill in skills)
        {
            if (string.IsNullOrWhiteSpace(skill.Name))
            {
                throw new InvalidOperationException("Aspire skills bundle manifest contains a skill without a name.");
            }

            if (!skillNames.Add(skill.Name))
            {
                throw new InvalidOperationException(string.Format(CultureInfo.InvariantCulture, "Aspire skills bundle manifest contains duplicate skill '{0}'.", skill.Name));
            }

            if (skill.Files is not { Length: > 0 })
            {
                throw new InvalidOperationException(string.Format(CultureInfo.InvariantCulture, "Aspire skills bundle skill '{0}' does not contain any files.", skill.Name));
            }

            foreach (var excludedPath in skill.InstallExcludedRelativePaths ?? [])
            {
                _ = NormalizeRelativePath(excludedPath);
            }

            foreach (var file in skill.Files)
            {
                ValidateFile(bundleDirectory, skill.Name, file);
            }
        }
    }

    private static void ValidateFile(DirectoryInfo bundleDirectory, string skillName, SkillBundleFile file)
    {
        var relativePath = NormalizeRelativePath(file.RelativePath);
        if (string.IsNullOrWhiteSpace(file.Sha256))
        {
            throw new InvalidOperationException(string.Format(CultureInfo.InvariantCulture, "Aspire skills bundle file '{0}' in skill '{1}' does not specify a SHA-256 hash.", relativePath, skillName));
        }

        var fullPath = Path.Combine(bundleDirectory.FullName, SkillsDirectoryName, skillName, relativePath);
        if (!File.Exists(fullPath))
        {
            throw new InvalidOperationException(string.Format(CultureInfo.InvariantCulture, "Aspire skills bundle file '{0}' in skill '{1}' was not found.", relativePath, skillName));
        }

        var expectedHash = NormalizeSha256(file.Sha256);
        using var stream = File.OpenRead(fullPath);
        var actualHash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        if (!string.Equals(expectedHash, actualHash, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(string.Format(CultureInfo.InvariantCulture, "Aspire skills bundle file '{0}' in skill '{1}' failed SHA-256 verification.", relativePath, skillName));
        }
    }

    private static bool ShouldInstallFile(SkillBundleSkill skill, string relativePath)
    {
        foreach (var excludedPath in skill.InstallExcludedRelativePaths ?? [])
        {
            var normalizedExcludedPath = NormalizeRelativePath(excludedPath);
            if (PathMatchesOrIsUnder(relativePath, normalizedExcludedPath))
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

    private static string NormalizeSha256(string sha256)
    {
        const string prefix = "sha256-";
        return sha256.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? sha256[prefix.Length..]
            : sha256;
    }
}
