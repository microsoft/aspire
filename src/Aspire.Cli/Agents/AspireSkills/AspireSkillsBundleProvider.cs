// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Formats.Tar;
using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Aspire.Cli.Resources;
using Aspire.Cli.Utils;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Semver;

namespace Aspire.Cli.Agents.AspireSkills;

/// <summary>
/// Provides one kind of Aspire Skills bundle.
/// </summary>
internal interface IAspireSkillsBundleProvider
{
    AgentAssetKind AssetKind { get; }

    string AssetKindName { get; }

    string AssetPrefix { get; }

    string CacheDirectoryName { get; }

    string DisplayName { get; }

    string ManifestFileName { get; }

    string ManifestAssetsPropertyName { get; }

    string ContentRootDirectoryName { get; }

    string RequiredFileName { get; }

    string EmbeddedArchiveResourceName { get; }

    string EmbeddedMetadataResourceName { get; }

    string VersionOverrideKey { get; }

    string DisablePackageValidationKey { get; }

    string MaxCacheAgeKey { get; }

    Task<AspireSkillsBundle> CreateAsync(
        FileInfo archive,
        DirectoryInfo bundleDirectory,
        string expectedArchiveSha512,
        CancellationToken cancellationToken,
        bool skipCompatibilityCheck = false);

    Task<AspireSkillsBundle> LoadAsync(
        DirectoryInfo bundleDirectory,
        CancellationToken cancellationToken,
        bool skipCompatibilityCheck = false);

    EmbeddedAspireSkillsBundleMetadata? GetEmbeddedMetadata();

    Task<AspireSkillsBundle?> CreateEmbeddedBundleAsync(
        DirectoryInfo bundleDirectory,
        CancellationToken cancellationToken);
}

/// <summary>
/// Creates and loads validated Aspire Skills bundles for one agent asset kind.
/// </summary>
/// <remarks>
/// This is the shared implementation for manifest loading, AOT-safe envelope mapping,
/// extraction, compatibility checks, safe names/paths, hashes, and bundle construction.
/// Concrete subclasses (see <see cref="SkillBundleProvider"/> and
/// <see cref="ExtensionBundleProvider"/>) contribute only kind-specific immutable metadata
/// and any genuinely different validation behavior via <see cref="ValidateRequiredFile"/>.
/// </remarks>
internal abstract class AspireSkillsBundleProvider : IAspireSkillsBundleProvider
{
    private const int MaxAssetNameLength = 64;

    private static readonly HashSet<string> s_textFileExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cjs",
        ".css",
        ".html",
        ".js",
        ".json",
        ".jsx",
        ".map",
        ".md",
        ".mjs",
        ".ps1",
        ".sh",
        ".svg",
        ".toml",
        ".ts",
        ".tsx",
        ".txt",
        ".xml",
        ".yaml",
        ".yml",
    };

    private readonly string _currentCliVersion;
    private readonly string _currentSdkVersion;
    private readonly ILogger _logger;
    private readonly Lazy<EmbeddedAspireSkillsBundleMetadata?> _embeddedMetadata;

    protected AspireSkillsBundleProvider(CliExecutionContext executionContext, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(executionContext);
        ArgumentNullException.ThrowIfNull(logger);

        // The CLI and SDK share one resolved identity version. IdentitySdkVersion removes
        // build metadata so both values can be compared with manifest SemVer ranges.
        _currentCliVersion = executionContext.IdentitySdkVersion;
        _currentSdkVersion = executionContext.IdentitySdkVersion;
        _logger = logger;
        _embeddedMetadata = new Lazy<EmbeddedAspireSkillsBundleMetadata?>(LoadEmbeddedMetadata);
    }

    protected AspireSkillsBundleProvider()
        : this(
            VersionHelper.GetDefaultSdkVersion(),
            VersionHelper.GetDefaultSdkVersion(),
            NullLogger.Instance)
    {
        // physical-binary-version-by-design (see docs/specs/cli-identity-sidecar.md):
        // this convenience constructor is only used by tests. Production resolves the
        // effective CLI identity through CliExecutionContext.
    }

    protected AspireSkillsBundleProvider(string currentCliVersion, string currentSdkVersion)
        : this(currentCliVersion, currentSdkVersion, NullLogger.Instance)
    {
    }

    private protected AspireSkillsBundleProvider(
        string currentCliVersion,
        string currentSdkVersion,
        ILogger logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(currentCliVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(currentSdkVersion);
        ArgumentNullException.ThrowIfNull(logger);

        _currentCliVersion = currentCliVersion;
        _currentSdkVersion = currentSdkVersion;
        _logger = logger;
        _embeddedMetadata = new Lazy<EmbeddedAspireSkillsBundleMetadata?>(LoadEmbeddedMetadata);
    }

    /// <summary>
    /// Gets the agent asset kind provided by this bundle provider.
    /// </summary>
    public abstract AgentAssetKind AssetKind { get; }

    public abstract string AssetKindName { get; }

    public abstract string AssetPrefix { get; }

    public abstract string CacheDirectoryName { get; }

    public abstract string DisplayName { get; }

    public abstract string ManifestFileName { get; }

    public abstract string ManifestAssetsPropertyName { get; }

    public abstract string ContentRootDirectoryName { get; }

    public abstract string RequiredFileName { get; }

    public abstract string EmbeddedArchiveResourceName { get; }

    public abstract string EmbeddedMetadataResourceName { get; }

    public string VersionOverrideKey => AspireSkillsInstaller.VersionOverrideKey;

    public string DisablePackageValidationKey => AspireSkillsInstaller.DisablePackageValidationKey;

    public string MaxCacheAgeKey => AspireSkillsInstaller.MaxCacheAgeKey;

    /// <summary>
    /// Creates an Aspire Skills bundle from an archive and materializes its validated files
    /// in a dedicated staging directory.
    /// </summary>
    public virtual async Task<AspireSkillsBundle> CreateAsync(
        FileInfo archive,
        DirectoryInfo bundleDirectory,
        string expectedArchiveSha512,
        CancellationToken cancellationToken,
        bool skipCompatibilityCheck = false)
    {
        ArgumentNullException.ThrowIfNull(archive);
        ArgumentNullException.ThrowIfNull(bundleDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedArchiveSha512);

        cancellationToken.ThrowIfCancellationRequested();
        ValidateArchiveSha512(archive.FullName, expectedArchiveSha512);

        Directory.CreateDirectory(bundleDirectory.FullName);
        var temporaryDirectoryRoot = bundleDirectory.Parent
            ?? throw new InvalidOperationException($"The {DisplayName} bundle staging directory must have a parent directory.");
        // Keep extraction beside the staging directory rather than inside it. If Windows AV or
        // indexing holds an extracted file open, best-effort cleanup must not block the later
        // atomic move that publishes the validated staging directory.
        using var extractionDirectory = TemporaryCacheDirectory.Create(
            temporaryDirectoryRoot.FullName,
            "extract",
            path => FileDeleteHelper.TryDeleteDirectory(path),
            path => FileDeleteHelper.TryDeleteFile(path));

        ExtractArchive(archive.FullName, extractionDirectory.FullName);
        cancellationToken.ThrowIfCancellationRequested();

        var bundleRoot = FindBundleRoot(extractionDirectory.FullName, ManifestFileName);
        var bundle = await LoadAsync(bundleRoot, cancellationToken, skipCompatibilityCheck).ConfigureAwait(false);

        CopyDirectory(bundleRoot.FullName, bundleDirectory.FullName);
        return bundle;
    }

    /// <summary>
    /// Loads an Aspire Skills bundle from disk.
    /// </summary>
    public async Task<AspireSkillsBundle> LoadAsync(
        DirectoryInfo bundleDirectory,
        CancellationToken cancellationToken,
        bool skipCompatibilityCheck = false)
    {
        ArgumentNullException.ThrowIfNull(bundleDirectory);

        var manifestPath = Path.Combine(bundleDirectory.FullName, ManifestFileName);
        if (!File.Exists(manifestPath))
        {
            throw new InvalidOperationException($"{DisplayName} bundle manifest was not found at '{manifestPath}'.");
        }

        SkillBundleManifest? manifest;
        try
        {
            await using var manifestStream = File.OpenRead(manifestPath);
            manifest = await JsonSerializer.DeserializeAsync(
                manifestStream,
                CreateManifestTypeInfo(),
                cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"{DisplayName} bundle manifest is invalid.", ex);
        }

        if (manifest is null)
        {
            throw new InvalidOperationException($"{DisplayName} bundle manifest is empty or invalid.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        return CreateBundle(bundleDirectory, manifest, skipCompatibilityCheck);
    }

    public virtual EmbeddedAspireSkillsBundleMetadata? GetEmbeddedMetadata()
    {
        return _embeddedMetadata.Value;
    }

    public virtual async Task<AspireSkillsBundle?> CreateEmbeddedBundleAsync(
        DirectoryInfo bundleDirectory,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(bundleDirectory);

        var metadata = GetEmbeddedMetadata();
        if (metadata is null || string.IsNullOrWhiteSpace(metadata.Sha512))
        {
            return null;
        }

        await using var archiveStream = OpenEmbeddedArchive();
        if (archiveStream is null)
        {
            return null;
        }

        Directory.CreateDirectory(bundleDirectory.FullName);
        var temporaryDirectoryRoot = bundleDirectory.Parent
            ?? throw new InvalidOperationException($"The {DisplayName} bundle staging directory must have a parent directory.");
        // Keep the archive beside the staging directory so a transient Windows file lock during
        // best-effort cleanup cannot prevent the validated staging directory from being published.
        using var temporaryDirectory = TemporaryCacheDirectory.Create(
            temporaryDirectoryRoot.FullName,
            "embedded",
            path => FileDeleteHelper.TryDeleteDirectory(path),
            path => FileDeleteHelper.TryDeleteFile(path));
        var archivePath = Path.Combine(temporaryDirectory.FullName, "bundle.tgz");

        await using (var fileStream = File.Create(archivePath))
        {
            await archiveStream.CopyToAsync(fileStream, cancellationToken).ConfigureAwait(false);
        }

        return await CreateAsync(
            new FileInfo(archivePath),
            bundleDirectory,
            metadata.Sha512,
            cancellationToken,
            skipCompatibilityCheck: true).ConfigureAwait(false);
    }

    private Stream? OpenEmbeddedArchive()
    {
        var stream = typeof(AspireSkillsBundleProvider).Assembly.GetManifestResourceStream(EmbeddedArchiveResourceName);
        if (stream is null)
        {
            _logger.LogDebug(
                "Embedded {BundleDisplayName} archive resource {ResourceName} was not found.",
                DisplayName,
                EmbeddedArchiveResourceName);
        }

        return stream;
    }

    private EmbeddedAspireSkillsBundleMetadata? LoadEmbeddedMetadata()
    {
        using var stream = typeof(AspireSkillsBundleProvider).Assembly.GetManifestResourceStream(EmbeddedMetadataResourceName);
        if (stream is null)
        {
            _logger.LogDebug(
                "Embedded {BundleDisplayName} metadata resource {ResourceName} was not found.",
                DisplayName,
                EmbeddedMetadataResourceName);
            return null;
        }

        try
        {
            var metadata = JsonSerializer.Deserialize(
                stream,
                AspireSkillsJsonSerializerContext.Default.EmbeddedAspireSkillsBundleMetadata);

            if (metadata is null)
            {
                _logger.LogDebug(
                    "Embedded {BundleDisplayName} metadata resource {ResourceName} was empty.",
                    DisplayName,
                    EmbeddedMetadataResourceName);
            }

            return metadata;
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(
                ex,
                "Embedded {BundleDisplayName} metadata resource {ResourceName} could not be parsed.",
                DisplayName,
                EmbeddedMetadataResourceName);
            return null;
        }
    }

    internal JsonTypeInfo<SkillBundleManifest> CreateManifestTypeInfo()
    {
        var manifestAssetsPropertyName = ManifestAssetsPropertyName;
        var resolver = AspireSkillsJsonSerializerContext.Default.WithAddedModifier(typeInfo =>
        {
            if (typeInfo.Type != typeof(SkillBundleManifest))
            {
                return;
            }

            var assetsPropertyName = JsonNamingPolicy.CamelCase.ConvertName(nameof(SkillBundleManifest.Assets));
            var assetsProperty = typeInfo.Properties.Single(property =>
                string.Equals(property.Name, assetsPropertyName, StringComparison.Ordinal));
            // Published manifests use a kind-specific top-level collection, such as:
            // { "skills": [...] }
            assetsProperty.Name = manifestAssetsPropertyName;
        });
        // Use the source-generated contract as the resolver base rather than
        // DefaultJsonTypeInfoResolver so the Native AOT CLI does not require reflection.
        var options = new JsonSerializerOptions(AspireSkillsJsonSerializerContext.Default.Options)
        {
            TypeInfoResolver = resolver,
        };

        return (JsonTypeInfo<SkillBundleManifest>)options.GetTypeInfo(typeof(SkillBundleManifest));
    }

    /// <summary>
    /// Validates the content of the bundle's required file (e.g., SKILL.md frontmatter).
    /// The base implementation performs no additional validation.
    /// </summary>
    protected virtual void ValidateRequiredFile(string assetName, ReadOnlySpan<byte> content)
    {
    }

    /// <summary>
    /// Gets how an installed file should be compared with its validated bundle bytes.
    /// </summary>
    protected virtual AgentAssetFileComparison GetFileComparison(string relativePath)
    {
        return s_textFileExtensions.Contains(Path.GetExtension(relativePath))
            ? AgentAssetFileComparison.NormalizedUtf8Text
            : AgentAssetFileComparison.ExactBytes;
    }

    private AspireSkillsBundle CreateBundle(
        DirectoryInfo bundleDirectory,
        SkillBundleManifest manifest,
        bool skipCompatibilityCheck)
    {
        var version = manifest.Version;
        if (string.IsNullOrWhiteSpace(version))
        {
            throw new InvalidOperationException($"{DisplayName} bundle manifest must specify a version.");
        }

        // The bundle's `supports` range gates remotely acquired bundles, including cache
        // entries that another CLI version may have written. The exact snapshot embedded
        // in the current CLI may skip this check because its stamped range can lag the
        // binary version (e.g., a dogfood build of 13.5.x using a snapshot stamped
        // ">=13.4.0 <13.5.0").
        if (!skipCompatibilityCheck)
        {
            ValidateCompatibility(manifest.Supports, _currentCliVersion, _currentSdkVersion);
        }

        var assets = manifest.Assets;
        if (assets is not { Length: > 0 })
        {
            throw new InvalidOperationException(
                $"{DisplayName} bundle manifest must contain at least one asset.");
        }

        var assetNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        List<ValidatedAspireSkillsBundleAsset> validatedAssets = [];
        foreach (var asset in assets)
        {
            if (asset is null)
            {
                throw new InvalidOperationException($"{DisplayName} bundle manifest contains an empty asset entry.");
            }

            var assetName = asset.Name;
            if (string.IsNullOrWhiteSpace(assetName))
            {
                throw new InvalidOperationException($"{DisplayName} bundle manifest contains an asset without a name.");
            }

            ValidateAssetName(assetName);
            if (!assetNames.Add(assetName))
            {
                throw new InvalidOperationException(
                    $"{DisplayName} bundle manifest contains duplicate asset '{assetName}'.");
            }

            if (string.IsNullOrWhiteSpace(asset.Description))
            {
                throw new InvalidOperationException(
                    $"{DisplayName} bundle asset '{assetName}' must specify a description.");
            }

            var assetFiles = asset.Files;
            if (assetFiles is not { Length: > 0 })
            {
                throw new InvalidOperationException(
                    $"{DisplayName} bundle asset '{assetName}' does not contain any files.");
            }

            var installExcludedRelativePaths = (asset.InstallExcludedRelativePaths ?? [])
                .Select(NormalizeRelativePath)
                .ToArray();
            if (installExcludedRelativePaths.Contains(RequiredFileName, StringComparer.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(string.Format(
                    CultureInfo.InvariantCulture,
                    "{0} bundle asset '{1}' cannot exclude {2} from installation.",
                    DisplayName,
                    assetName,
                    RequiredFileName));
            }

            var definition = AgentFileAssetDefinition.CreateAspireSkillsBundle(
                AssetKind,
                assetName,
                asset.Description,
                installExcludedRelativePaths,
                asset.ApplicableLanguages ?? []);

            var filePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var hasRequiredFile = false;
            List<AgentAssetFile> files = [];
            foreach (var file in assetFiles)
            {
                if (file is null)
                {
                    throw new InvalidOperationException(
                        $"{DisplayName} bundle asset '{assetName}' contains an empty file entry.");
                }

                var validatedFile = ValidateFile(bundleDirectory, assetName, file);
                if (!filePaths.Add(validatedFile.RelativePath))
                {
                    throw new InvalidOperationException(string.Format(
                        CultureInfo.InvariantCulture,
                        "{0} bundle asset '{1}' contains duplicate file '{2}'.",
                        DisplayName,
                        assetName,
                        validatedFile.RelativePath));
                }

                files.Add(validatedFile);
                hasRequiredFile |= string.Equals(validatedFile.RelativePath, RequiredFileName, StringComparison.Ordinal);
            }

            if (!hasRequiredFile)
            {
                throw new InvalidOperationException(string.Format(
                    CultureInfo.InvariantCulture,
                    "{0} bundle asset '{1}' must contain {2}.",
                    DisplayName,
                    assetName,
                    RequiredFileName));
            }

            validatedAssets.Add(new ValidatedAspireSkillsBundleAsset(definition, files));
        }

        return new AspireSkillsBundle(version, AssetKind, validatedAssets);
    }

    private void ValidateAssetName(string assetName)
    {
        // Agent hosts use this portable grammar to discover asset directories consistently.
        // See https://agentskills.io/specification.
        if (assetName.Length > MaxAssetNameLength ||
            assetName[0] == '-' ||
            assetName[^1] == '-' ||
            assetName.Contains("--", StringComparison.Ordinal) ||
            assetName.Any(static character => !char.IsAsciiLetterLower(character) && !char.IsAsciiDigit(character) && character is not '-'))
        {
            throw new InvalidOperationException(string.Format(
                CultureInfo.InvariantCulture,
                "{0} bundle asset name '{1}' must be 1-{2} characters, use only lowercase ASCII letters, digits, and hyphens, and must not start or end with a hyphen or contain consecutive hyphens.",
                DisplayName,
                assetName,
                MaxAssetNameLength));
        }

        if (IsWindowsDeviceName(assetName))
        {
            throw new InvalidOperationException(
                $"{DisplayName} bundle asset name '{assetName}' is not portable.");
        }
    }

    private AgentAssetFile ValidateFile(
        DirectoryInfo bundleDirectory,
        string assetName,
        SkillBundleFile file)
    {
        var relativePath = NormalizeRelativePath(file.RelativePath);
        var fullPath = Path.Combine(bundleDirectory.FullName, ContentRootDirectoryName, assetName, relativePath);
        if (!File.Exists(fullPath))
        {
            throw new InvalidOperationException(
                $"{DisplayName} bundle file '{relativePath}' in asset '{assetName}' was not found.");
        }

        // Hash and decode the same bytes so a concurrent filesystem change cannot
        // produce validated content that differs from the content retained in memory.
        var bytes = File.ReadAllBytes(fullPath);
        string expectedHash;
        string actualHash;
        string algorithmName;
        // Prefer SHA-512 when the manifest provides it. SHA-256 remains supported for published
        // Aspire Skills bundles whose attested archive manifests use that digest.
        if (!string.IsNullOrWhiteSpace(file.Sha512))
        {
            expectedHash = NormalizeSha512(file.Sha512);
            actualHash = Convert.ToHexString(SHA512.HashData(bytes)).ToLowerInvariant();
            algorithmName = "SHA-512";
        }
        else if (!string.IsNullOrWhiteSpace(file.Sha256))
        {
            expectedHash = NormalizeSha256(file.Sha256);
            actualHash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            algorithmName = "SHA-256";
        }
        else
        {
            throw new InvalidOperationException(
                $"{DisplayName} bundle file '{relativePath}' in asset '{assetName}' does not specify a SHA-512 or SHA-256 hash.");
        }

        if (!string.Equals(expectedHash, actualHash, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"{DisplayName} bundle file '{relativePath}' in asset '{assetName}' failed {algorithmName} verification.");
        }

        var comparison = GetFileComparison(relativePath);
        if (string.Equals(relativePath, RequiredFileName, StringComparison.Ordinal))
        {
            ValidateRequiredFile(assetName, bytes);
        }

        return new AgentAssetFile(relativePath, bytes, comparison);
    }

    internal string NormalizeRelativePath(string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            throw new InvalidOperationException($"{DisplayName} bundle contains an empty relative path.");
        }

        var normalizedPath = relativePath
            .Replace('/', Path.DirectorySeparatorChar)
            .Replace('\\', Path.DirectorySeparatorChar);

        if (Path.IsPathRooted(normalizedPath))
        {
            throw new InvalidOperationException($"{DisplayName} bundle path '{relativePath}' must be relative.");
        }

        var segments = normalizedPath.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0 || segments.Any(static segment => !IsPortablePathSegment(segment)))
        {
            throw new InvalidOperationException($"{DisplayName} bundle path '{relativePath}' is not safe.");
        }

        return Path.Combine(segments);
    }

    private static bool IsPortablePathSegment(string segment)
    {
        // Bundle paths can be validated on one platform and installed on another. Reject the
        // Windows-invalid character set, trailing dot/space aliases, and DOS device names
        // everywhere so a bundle accepted on Unix cannot fail or address a different file on
        // Windows. Device names remain reserved when followed by an extension (for example,
        // "NUL.txt"), so compare only the portion before the first dot.
        // See https://learn.microsoft.com/windows/win32/fileio/naming-a-file.
        return segment is not "." and not ".." &&
            !segment.EndsWith('.') &&
            !segment.EndsWith(' ') &&
            !segment.Any(static character => char.IsControl(character) || character is '<' or '>' or ':' or '"' or '|' or '?' or '*') &&
            !IsWindowsDeviceName(segment.Split('.')[0]);
    }

    private static bool IsWindowsDeviceName(string name)
    {
        if (name.Equals("CON", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("AUX", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("NUL", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("CLOCK$", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("CONIN$", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("CONOUT$", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return name.Length == 4 &&
            name[3] is >= '1' and <= '9' &&
            (name.StartsWith("COM", StringComparison.OrdinalIgnoreCase) ||
             name.StartsWith("LPT", StringComparison.OrdinalIgnoreCase));
    }

    internal static string NormalizeSha512(string sha512)
    {
        return sha512.StartsWith("sha512-", StringComparison.OrdinalIgnoreCase) ||
            sha512.StartsWith("sha512:", StringComparison.OrdinalIgnoreCase)
                ? sha512[7..]
                : sha512;
    }

    internal static string NormalizeSha256(string sha256)
    {
        return sha256.StartsWith("sha256-", StringComparison.OrdinalIgnoreCase) ||
            sha256.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)
                ? sha256[7..]
                : sha256;
    }

    private void ValidateArchiveSha512(string archivePath, string expectedSha512)
    {
        var expectedHash = NormalizeSha512(expectedSha512);
        using var stream = File.OpenRead(archivePath);
        var actualHash = Convert.ToHexString(SHA512.HashData(stream)).ToLowerInvariant();

        if (!string.Equals(expectedHash, actualHash, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(string.Format(
                CultureInfo.CurrentCulture,
                AgentCommandStrings.AspireSkillsInstaller_ArchiveHashVerificationFailed,
                DisplayName,
                expectedHash,
                actualHash));
        }
    }

    private void ExtractArchive(string archivePath, string destinationDirectory)
    {
        if (archivePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            ExtractZipArchive(archivePath, destinationDirectory);
            return;
        }

        ExtractTarball(archivePath, destinationDirectory);
    }

    private void ExtractTarball(string tarballPath, string destinationDirectory)
    {
        var destinationRoot = Path.GetFullPath(destinationDirectory);
        Directory.CreateDirectory(destinationRoot);

        using var fileStream = File.OpenRead(tarballPath);
        using var gzipStream = new GZipStream(fileStream, CompressionMode.Decompress);
        using var tarReader = new TarReader(gzipStream);

        while (tarReader.GetNextEntry() is { } entry)
        {
            if (string.IsNullOrWhiteSpace(entry.Name))
            {
                continue;
            }

            var destinationPath = GetSafeArchiveDestinationPath(destinationRoot, entry.Name);

            switch (entry.EntryType)
            {
                case TarEntryType.Directory:
                    Directory.CreateDirectory(destinationPath);
                    break;

                case TarEntryType.RegularFile:
                case TarEntryType.V7RegularFile:
                    var destinationFileDirectory = Path.GetDirectoryName(destinationPath);
                    if (!string.IsNullOrEmpty(destinationFileDirectory))
                    {
                        Directory.CreateDirectory(destinationFileDirectory);
                    }

                    entry.ExtractToFile(destinationPath, overwrite: false);
                    break;

                case TarEntryType.GlobalExtendedAttributes:
                case TarEntryType.ExtendedAttributes:
                    break;

                default:
                    throw new InvalidDataException(
                        $"{DisplayName} bundle archive entry '{entry.Name}' has unsupported type '{entry.EntryType}'.");
            }
        }
    }

    private void ExtractZipArchive(string archivePath, string destinationDirectory)
    {
        var destinationRoot = Path.GetFullPath(destinationDirectory);
        Directory.CreateDirectory(destinationRoot);

        using var archive = ZipFile.OpenRead(archivePath);
        foreach (var entry in archive.Entries)
        {
            if (string.IsNullOrWhiteSpace(entry.FullName))
            {
                continue;
            }

            var destinationPath = GetSafeArchiveDestinationPath(destinationRoot, entry.FullName);
            if (entry.FullName.EndsWith("/", StringComparison.Ordinal) || entry.FullName.EndsWith("\\", StringComparison.Ordinal))
            {
                Directory.CreateDirectory(destinationPath);
                continue;
            }

            var destinationFileDirectory = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrEmpty(destinationFileDirectory))
            {
                Directory.CreateDirectory(destinationFileDirectory);
            }

            entry.ExtractToFile(destinationPath, overwrite: false);
        }
    }

    private string GetSafeArchiveDestinationPath(string destinationRoot, string entryName)
    {
        var normalizedEntryName = entryName.Replace('\\', '/');
        var segments = normalizedEntryName.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (Path.IsPathRooted(normalizedEntryName) ||
            segments.Length == 0 ||
            segments.Any(static segment => !IsPortablePathSegment(segment)))
        {
            throw new InvalidDataException($"{DisplayName} bundle archive entry '{entryName}' is not safe.");
        }

        var destinationPath = Path.GetFullPath(Path.Combine(destinationRoot, normalizedEntryName.Replace('/', Path.DirectorySeparatorChar)));
        if (!destinationPath.StartsWith(destinationRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal) &&
            !string.Equals(destinationPath, destinationRoot, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"{DisplayName} bundle archive entry '{entryName}' escapes the extraction directory.");
        }

        return destinationPath;
    }

    private DirectoryInfo FindBundleRoot(string extractionDirectory, string manifestFileName)
    {
        var rootManifestPath = Path.Combine(extractionDirectory, manifestFileName);
        if (File.Exists(rootManifestPath))
        {
            return new DirectoryInfo(extractionDirectory);
        }

        var packageDirectory = Path.Combine(extractionDirectory, "package");
        var packageManifestPath = Path.Combine(packageDirectory, manifestFileName);
        if (File.Exists(packageManifestPath))
        {
            return new DirectoryInfo(packageDirectory);
        }

        var topLevelBundleDirectories = Directory
            .EnumerateDirectories(extractionDirectory)
            .Where(directory => File.Exists(Path.Combine(directory, manifestFileName)))
            .ToArray();

        if (topLevelBundleDirectories.Length == 1)
        {
            return new DirectoryInfo(topLevelBundleDirectories[0]);
        }

        if (topLevelBundleDirectories.Length > 1)
        {
            throw new InvalidOperationException(string.Format(
                CultureInfo.InvariantCulture,
                "Downloaded {0} bundle contains multiple '{1}' files.",
                DisplayName,
                manifestFileName));
        }

        throw new InvalidOperationException(string.Format(
            CultureInfo.InvariantCulture,
            "Downloaded {0} bundle does not contain '{1}'.",
            DisplayName,
            manifestFileName));
    }

    private static void CopyDirectory(string sourceDirectory, string targetDirectory)
    {
        Directory.CreateDirectory(targetDirectory);

        foreach (var sourceFile in Directory.GetFiles(sourceDirectory, "*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(sourceDirectory, sourceFile);
            var targetFile = Path.Combine(targetDirectory, relativePath);
            var targetFileDirectory = Path.GetDirectoryName(targetFile);
            if (!string.IsNullOrEmpty(targetFileDirectory))
            {
                Directory.CreateDirectory(targetFileDirectory);
            }

            File.Copy(sourceFile, targetFile, overwrite: true);
        }
    }

    private void ValidateCompatibility(SkillBundleSupports? supports, string currentCliVersion, string currentSdkVersion)
    {
        if (supports is null)
        {
            throw new InvalidOperationException($"{DisplayName} bundle manifest must specify supported Aspire versions.");
        }

        if (string.IsNullOrWhiteSpace(supports.AspireCli))
        {
            throw new InvalidOperationException($"{DisplayName} bundle manifest must specify supports.aspireCli.");
        }

        if (!IsVersionInRange(currentCliVersion, supports.AspireCli))
        {
            throw new InvalidOperationException(string.Format(
                CultureInfo.InvariantCulture,
                "{0} bundle supports Aspire CLI versions '{1}', but the current CLI version is '{2}'.",
                DisplayName,
                supports.AspireCli,
                currentCliVersion));
        }

        if (!string.IsNullOrWhiteSpace(supports.AspireSdk) &&
            !IsVersionInRange(currentSdkVersion, supports.AspireSdk))
        {
            throw new InvalidOperationException(string.Format(
                CultureInfo.InvariantCulture,
                "{0} bundle supports Aspire SDK versions '{1}', but the current SDK version is '{2}'.",
                DisplayName,
                supports.AspireSdk,
                currentSdkVersion));
        }
    }

    private bool IsVersionInRange(string version, string range)
    {
        var normalizedVersion = ParseCompatibilityVersion(version);
        var comparators = range.Replace(',', ' ').Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (comparators.Length == 0)
        {
            throw new InvalidOperationException($"{DisplayName} bundle contains an empty version range.");
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

    private bool SatisfiesComparator(SemVersion version, string comparator)
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
            _ => throw new InvalidOperationException($"{DisplayName} bundle contains unsupported version comparator '{op}'.")
        };
    }

    private (string Operator, string Operand) ParseComparator(string comparator)
    {
        foreach (var op in new[] { ">=", "<=", "==", ">", "<", "=" })
        {
            if (comparator.StartsWith(op, StringComparison.Ordinal))
            {
                var operand = comparator[op.Length..];
                if (string.IsNullOrWhiteSpace(operand))
                {
                    throw new InvalidOperationException($"{DisplayName} bundle contains an invalid version comparator '{comparator}'.");
                }

                return (op, operand);
            }
        }

        return ("=", comparator);
    }

    private SemVersion ParseCompatibilityVersion(string version)
    {
        if (!SemVersion.TryParse(version, SemVersionStyles.Any, out var parsedVersion))
        {
            throw new InvalidOperationException($"{DisplayName} bundle contains an invalid version value '{version}'.");
        }

        return SemVersion.Parse(
            string.Create(
                CultureInfo.InvariantCulture,
                $"{parsedVersion.Major}.{parsedVersion.Minor}.{parsedVersion.Patch}"),
            SemVersionStyles.Strict);
    }
}
