// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using System.Security.Cryptography;
using Aspire.Cli.Utils;
using Semver;

namespace Aspire.Cli.Agents.AspireSkills;

/// <summary>
/// Represents a validated Aspire skills bundle on disk.
/// </summary>
internal sealed class AspireSkillsBundle
{
    private const string ManifestFileName = "skill-manifest.json";
    private const string SkillsDirectoryName = "skills";
    private const string SkillFileName = "SKILL.md";
    private const int MaxSkillDescriptionLength = 1024;

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
        return await LoadAsync(
            bundleDirectory,
            VersionHelper.GetDefaultSdkVersion(),
            VersionHelper.GetDefaultSdkVersion(),
            cancellationToken).ConfigureAwait(false);
    }

    internal static async Task<AspireSkillsBundle> LoadAsync(
        DirectoryInfo bundleDirectory,
        string currentCliVersion,
        string currentSdkVersion,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(bundleDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(currentCliVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(currentSdkVersion);

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

        ValidateManifest(bundleDirectory, manifest, currentCliVersion, currentSdkVersion);

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

    private static void ValidateManifest(
        DirectoryInfo bundleDirectory,
        SkillBundleManifest manifest,
        string currentCliVersion,
        string currentSdkVersion)
    {
        if (string.IsNullOrWhiteSpace(manifest.Version))
        {
            throw new InvalidOperationException("Aspire skills bundle manifest must specify a version.");
        }

        ValidateCompatibility(manifest.Supports, currentCliVersion, currentSdkVersion);

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
        string actualHash;
        using (var stream = File.OpenRead(fullPath))
        {
            actualHash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        }

        if (!string.Equals(expectedHash, actualHash, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(string.Format(CultureInfo.InvariantCulture, "Aspire skills bundle file '{0}' in skill '{1}' failed SHA-256 verification.", relativePath, skillName));
        }

        if (string.Equals(relativePath, SkillFileName, StringComparison.Ordinal))
        {
            ValidateSkillFileFrontmatter(skillName, fullPath);
        }
    }

    private static void ValidateSkillFileFrontmatter(string skillName, string skillFilePath)
    {
        var content = File.ReadAllText(skillFilePath);
        var description = GetFrontmatterValue(content, "description");
        if (string.IsNullOrWhiteSpace(description))
        {
            throw new InvalidOperationException(string.Format(CultureInfo.InvariantCulture, "Aspire skills bundle skill '{0}' must define a frontmatter description in SKILL.md.", skillName));
        }

        if (description.Length > MaxSkillDescriptionLength)
        {
            throw new InvalidOperationException(string.Format(
                CultureInfo.InvariantCulture,
                "Aspire skills bundle skill '{0}' SKILL.md description is {1} characters; agent hosts accept at most {2}.",
                skillName,
                description.Length,
                MaxSkillDescriptionLength));
        }
    }

    private static string? GetFrontmatterValue(string content, string key)
    {
        var normalizedContent = content.ReplaceLineEndings("\n");
        if (!normalizedContent.StartsWith("---\n", StringComparison.Ordinal))
        {
            return null;
        }

        var frontmatterEndIndex = normalizedContent.IndexOf("\n---\n", 4, StringComparison.Ordinal);
        if (frontmatterEndIndex < 0)
        {
            return null;
        }

        // Skill files use simple YAML frontmatter:
        //   ---
        //   name: aspire
        //   description: "Use when working with an Aspire distributed application"
        //   ---
        // The agent hosts read this field directly and reject descriptions longer
        // than 1024 characters, so validate the bundled SKILL.md before caching it.
        var frontmatter = normalizedContent[4..frontmatterEndIndex];
        var keyPrefix = $"{key}:";
        foreach (var line in frontmatter.Split('\n'))
        {
            if (!line.StartsWith(keyPrefix, StringComparison.Ordinal))
            {
                continue;
            }

            var value = line[keyPrefix.Length..].Trim();
            return value.Length >= 2 &&
                   ((value[0] == '"' && value[^1] == '"') || (value[0] == '\'' && value[^1] == '\''))
                ? value[1..^1]
                : value;
        }

        return null;
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

    internal static string NormalizeSha256(string sha256)
    {
        const string prefix = "sha256-";
        return sha256.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? sha256[prefix.Length..]
            : sha256;
    }

    private static void ValidateCompatibility(SkillBundleSupports? supports, string currentCliVersion, string currentSdkVersion)
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
            throw new InvalidOperationException(string.Format(
                CultureInfo.InvariantCulture,
                "Aspire skills bundle supports Aspire CLI versions '{0}', but the current CLI version is '{1}'.",
                supports.AspireCli,
                currentCliVersion));
        }

        if (!string.IsNullOrWhiteSpace(supports.AspireSdk) &&
            !IsVersionInRange(currentSdkVersion, supports.AspireSdk))
        {
            throw new InvalidOperationException(string.Format(
                CultureInfo.InvariantCulture,
                "Aspire skills bundle supports Aspire SDK versions '{0}', but the current SDK version is '{1}'.",
                supports.AspireSdk,
                currentSdkVersion));
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
        var operand = ParseCompatibilityVersion(operandText);
        var comparison = SemVersion.ComparePrecedence(version, operand);

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

        return SemVersion.Parse(
            string.Create(
                CultureInfo.InvariantCulture,
                $"{parsedVersion.Major}.{parsedVersion.Minor}.{parsedVersion.Patch}"),
            SemVersionStyles.Strict);
    }
}
