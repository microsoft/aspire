// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Aspire.Cli.Utils;
using Semver;

namespace Aspire.Cli.Agents.AspireSkills;

/// <summary>
/// Loads Aspire skills bundles from disk.
/// </summary>
internal interface IAspireSkillsBundleProvider
{
    /// <summary>
    /// Gets the manifest file name used by bundles for the specified asset kind.
    /// </summary>
    string GetManifestFileName(AgentAssetKind assetKind);

    /// <summary>
    /// Loads and validates a bundle using the current CLI version defaults.
    /// </summary>
    Task<AspireSkillsBundle> LoadAsync(DirectoryInfo bundleDirectory, AgentAssetKind assetKind, CancellationToken cancellationToken);

    /// <summary>
    /// Loads and validates a bundle using explicit CLI and SDK versions.
    /// </summary>
    Task<AspireSkillsBundle> LoadAsync(
        DirectoryInfo bundleDirectory,
        AgentAssetKind assetKind,
        string currentCliVersion,
        string currentSdkVersion,
        CancellationToken cancellationToken);

    /// <summary>
    /// Loads and validates a bundle, optionally skipping CLI and SDK compatibility checks.
    /// </summary>
    Task<AspireSkillsBundle> LoadAsync(
        DirectoryInfo bundleDirectory,
        AgentAssetKind assetKind,
        string currentCliVersion,
        string currentSdkVersion,
        bool skipCompatibilityCheck,
        CancellationToken cancellationToken);
}

internal sealed class AspireSkillsBundleProvider : IAspireSkillsBundleProvider
{
    private const string SkillFileName = "SKILL.md";
    private const string ExtensionEntryFileName = "extension.mjs";
    private const int MaxSkillDescriptionLength = 1024;

    private static readonly BundleLayout s_skillsBundleLayout = new(
        AgentAssetKind.Skill,
        manifestPropertyName: "skills",
        relativeDirectoryName: "skills",
        manifestFileName: "skill-manifest.json",
        validateFileFrontmatter: ValidateSkillFileFrontmatter);

    private static readonly BundleLayout s_extensionsBundleLayout = new(
        AgentAssetKind.Extension,
        manifestPropertyName: "extensions",
        relativeDirectoryName: "extensions",
        manifestFileName: "extension-manifest.json",
        validateAssetFiles: ValidateExtensionAssetFiles);

    public string GetManifestFileName(AgentAssetKind assetKind)
    {
        return GetBundleLayout(assetKind).ManifestFileName;
    }

    public async Task<AspireSkillsBundle> LoadAsync(DirectoryInfo bundleDirectory, AgentAssetKind assetKind, CancellationToken cancellationToken)
    {
        // physical-binary-version-by-design (see docs/specs/cli-identity-sidecar.md):
        // this convenience overload is only used by tests, which assert against
        // the running test-host assembly version. Production paths (AspireSkillsInstaller) flow
        // CliExecutionContext.IdentitySdkVersion through the explicit-version overload instead.
        return await LoadAsync(
            bundleDirectory,
            assetKind,
            VersionHelper.GetDefaultSdkVersion(),
            VersionHelper.GetDefaultSdkVersion(),
            skipCompatibilityCheck: false,
            cancellationToken).ConfigureAwait(false);
    }

    public Task<AspireSkillsBundle> LoadAsync(
        DirectoryInfo bundleDirectory,
        AgentAssetKind assetKind,
        string currentCliVersion,
        string currentSdkVersion,
        CancellationToken cancellationToken)
    {
        return LoadAsync(bundleDirectory, assetKind, currentCliVersion, currentSdkVersion, skipCompatibilityCheck: false, cancellationToken);
    }

    public async Task<AspireSkillsBundle> LoadAsync(
        DirectoryInfo bundleDirectory,
        AgentAssetKind assetKind,
        string currentCliVersion,
        string currentSdkVersion,
        bool skipCompatibilityCheck,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(bundleDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(currentCliVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(currentSdkVersion);

        var layout = GetBundleLayout(assetKind);
        var manifestPath = Path.Combine(bundleDirectory.FullName, layout.ManifestFileName);
        if (!File.Exists(manifestPath))
        {
            throw new InvalidOperationException(string.Format(CultureInfo.InvariantCulture, "Aspire skills bundle manifest was not found at '{0}'.", manifestPath));
        }

        await using var manifestStream = File.OpenRead(manifestPath);
        var manifestJson = await JsonSerializer.DeserializeAsync(
            manifestStream,
            AspireSkillsJsonSerializerContext.Default.SkillBundleManifestJson,
            cancellationToken).ConfigureAwait(false);

        if (manifestJson is null)
        {
            throw new InvalidOperationException("Aspire skills bundle manifest is empty or invalid.");
        }

        var manifest = ToValidatedManifest(bundleDirectory, layout, manifestJson, currentCliVersion, currentSdkVersion, skipCompatibilityCheck);

        return new AspireSkillsBundle(bundleDirectory, manifest, layout.AssetKind, layout.RelativeDirectoryName);
    }

    private static BundleLayout GetBundleLayout(AgentAssetKind assetKind)
    {
        return assetKind switch
        {
            AgentAssetKind.Skill => s_skillsBundleLayout,
            AgentAssetKind.Extension => s_extensionsBundleLayout,
            _ => throw new InvalidOperationException($"Unsupported agent asset kind '{assetKind}'.")
        };
    }

    private static SkillBundleManifest ToValidatedManifest(
        DirectoryInfo bundleDirectory,
        BundleLayout layout,
        SkillBundleManifestJson manifestJson,
        string currentCliVersion,
        string currentSdkVersion,
        bool skipCompatibilityCheck)
    {
        var manifest = new SkillBundleManifest
        {
            Version = manifestJson.Version,
            Supports = manifestJson.Supports,
            Assets = ValidateAndNormalizeAssets(GetManifestAssets(layout, manifestJson), bundleDirectory, layout)
        };

        ValidateManifest(manifest, currentCliVersion, currentSdkVersion, skipCompatibilityCheck);

        return manifest;
    }

    private static SkillBundleAsset[] GetManifestAssets(BundleLayout layout, SkillBundleManifestJson manifestJson)
    {
        var hasSkills = manifestJson.Skills is not null;
        var hasExtensions = manifestJson.Extensions is not null;
        if (hasSkills == hasExtensions)
        {
            throw new JsonException("Aspire skills bundle manifest must contain exactly one of 'skills' or 'extensions'.");
        }

        var actualAssetKind = hasSkills ? AgentAssetKind.Skill : AgentAssetKind.Extension;
        if (actualAssetKind != layout.AssetKind)
        {
            throw new JsonException(string.Format(
                CultureInfo.InvariantCulture,
                "Aspire skills bundle manifest contains '{0}', but '{1}' was expected.",
                hasSkills ? "skills" : "extensions",
                layout.ManifestPropertyName));
        }

        return hasSkills ? manifestJson.Skills! : manifestJson.Extensions!;
    }

    private static void ValidateManifest(
        SkillBundleManifest manifest,
        string currentCliVersion,
        string currentSdkVersion,
        bool skipCompatibilityCheck)
    {
        if (string.IsNullOrWhiteSpace(manifest.Version))
        {
            throw new InvalidOperationException("Aspire skills bundle manifest must specify a version.");
        }

        // The bundle's `supports` range gates whether a bundle pulled fresh from GitHub
        // is allowed at runtime. For bundles we already trust locally - the snapshot
        // embedded in the CLI binary, and bundles already written to our own cache -
        // we skip the range check because the CLI's effective version may have moved
        // past the snapshot's stamped range (e.g., a dogfood build of 13.5.x using a
        // bundle whose supports declares ">=13.4.0 <13.5.0"). The bundle's `version`
        // field plus the version-keyed cache directory still gate matching content.
        if (!skipCompatibilityCheck)
        {
            ValidateCompatibility(manifest.Supports, currentCliVersion, currentSdkVersion);
        }

        if (manifest.Assets is not { Length: > 0 })
        {
            throw new InvalidOperationException("Aspire skills bundle manifest must contain at least one asset.");
        }
    }

    private static SkillBundleAsset[] ValidateAndNormalizeAssets(
        SkillBundleAsset[] assets,
        DirectoryInfo bundleDirectory,
        BundleLayout layout)
    {
        var assetNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var normalizedAssets = new List<SkillBundleAsset>();
        foreach (var asset in assets)
        {
            if (asset is null)
            {
                throw new InvalidOperationException("Aspire skills bundle manifest contains a null asset.");
            }

            if (string.IsNullOrWhiteSpace(asset.Name))
            {
                throw new InvalidOperationException("Aspire skills bundle manifest contains an asset without a name.");
            }

            if (!assetNames.Add(asset.Name))
            {
                throw new InvalidOperationException(string.Format(CultureInfo.InvariantCulture, "Aspire skills bundle manifest contains duplicate asset '{0}'.", asset.Name));
            }

            if (string.IsNullOrWhiteSpace(asset.Description))
            {
                throw new InvalidOperationException(string.Format(CultureInfo.InvariantCulture, "Aspire skills bundle asset '{0}' must specify a description.", asset.Name));
            }

            if (asset.Files is not { Length: > 0 })
            {
                throw new InvalidOperationException(string.Format(CultureInfo.InvariantCulture, "Aspire skills bundle asset '{0}' does not contain any files.", asset.Name));
            }

            var normalizedExcludedPaths = (asset.InstallExcludedRelativePaths ?? [])
                .Select(NormalizeRelativePath)
                .ToArray();

            var normalizedFiles = new List<SkillBundleFile>();
            var normalizedRelativePaths = new List<string>();
            foreach (var file in asset.Files)
            {
                if (file is null)
                {
                    throw new InvalidOperationException(string.Format(CultureInfo.InvariantCulture, "Aspire skills bundle asset '{0}' contains a null file.", asset.Name));
                }

                var normalizedFile = ValidateAndNormalizeFile(bundleDirectory, layout, asset.Name, file);
                normalizedFiles.Add(normalizedFile);
                normalizedRelativePaths.Add(normalizedFile.RelativePath!);
            }

            var normalizedAsset = new SkillBundleAsset
            {
                Name = asset.Name,
                Description = asset.Description,
                ApplicableLanguages = asset.ApplicableLanguages ?? [],
                InstallExcludedRelativePaths = normalizedExcludedPaths,
                Files = normalizedFiles.ToArray()
            };

            layout.ValidateAssetFiles(normalizedAsset, normalizedRelativePaths);
            normalizedAssets.Add(normalizedAsset);
        }

        return normalizedAssets.ToArray();
    }

    private static SkillBundleFile ValidateAndNormalizeFile(
        DirectoryInfo bundleDirectory,
        BundleLayout layout,
        string assetName,
        SkillBundleFile file)
    {
        var relativePath = NormalizeRelativePath(file.RelativePath);
        if (string.IsNullOrWhiteSpace(file.Sha256))
        {
            throw new InvalidOperationException(string.Format(CultureInfo.InvariantCulture, "Aspire skills bundle file '{0}' in asset '{1}' does not specify a SHA-256 hash.", relativePath, assetName));
        }

        var fullPath = Path.Combine(bundleDirectory.FullName, layout.RelativeDirectoryName, assetName, relativePath);
        if (!File.Exists(fullPath))
        {
            throw new InvalidOperationException(string.Format(CultureInfo.InvariantCulture, "Aspire skills bundle file '{0}' in asset '{1}' was not found.", relativePath, assetName));
        }

        var expectedHash = NormalizeSha256(file.Sha256);
        string actualHash;
        using (var stream = File.OpenRead(fullPath))
        {
            actualHash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        }

        if (!string.Equals(expectedHash, actualHash, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(string.Format(CultureInfo.InvariantCulture, "Aspire skills bundle file '{0}' in asset '{1}' failed SHA-256 verification.", relativePath, assetName));
        }

        layout.ValidateFileFrontmatter(assetName, fullPath);

        return new SkillBundleFile
        {
            RelativePath = relativePath,
            Sha256 = expectedHash
        };
    }

    private static void ValidateSkillFileFrontmatter(string assetName, string fullPath)
    {
        if (!string.Equals(Path.GetFileName(fullPath), SkillFileName, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var content = File.ReadAllText(fullPath);
        var description = GetFrontmatterValue(content, "description");
        if (string.IsNullOrWhiteSpace(description))
        {
            throw new InvalidOperationException(string.Format(CultureInfo.InvariantCulture, "Aspire skills bundle skill '{0}' must define a frontmatter description in SKILL.md.", assetName));
        }

        if (description.Length > MaxSkillDescriptionLength)
        {
            throw new InvalidOperationException(string.Format(
                CultureInfo.InvariantCulture,
                "Aspire skills bundle skill '{0}' SKILL.md description is {1} characters; agent hosts accept at most {2}.",
                assetName,
                description.Length,
                MaxSkillDescriptionLength));
        }
    }

    private static void ValidateExtensionAssetFiles(SkillBundleAsset asset, IReadOnlyList<string> relativePaths)
    {
        if (!relativePaths.Contains(ExtensionEntryFileName, StringComparer.Ordinal))
        {
            throw new InvalidOperationException(string.Format(CultureInfo.InvariantCulture, "Aspire skills bundle extension '{0}' must include '{1}'.", asset.Name, ExtensionEntryFileName));
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

    /// <summary>
    /// Describes the layout of a bundle for one kind of Aspire skills asset bundle.
    /// </summary>
    private sealed class BundleLayout(
        AgentAssetKind assetKind,
        string manifestPropertyName,
        string relativeDirectoryName,
        string manifestFileName,
        Action<string, string>? validateFileFrontmatter = null,
        Action<SkillBundleAsset, IReadOnlyList<string>>? validateAssetFiles = null)
    {
        public AgentAssetKind AssetKind { get; } = assetKind;

        public string ManifestPropertyName { get; } = manifestPropertyName;

        public string RelativeDirectoryName { get; } = relativeDirectoryName;

        public string ManifestFileName { get; } = manifestFileName;

        public void ValidateFileFrontmatter(string assetName, string fullPath)
        {
            validateFileFrontmatter?.Invoke(assetName, fullPath);
        }

        public void ValidateAssetFiles(SkillBundleAsset asset, IReadOnlyList<string> relativePaths)
        {
            validateAssetFiles?.Invoke(asset, relativePaths);
        }
    }
}
