// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Aspire.Cli.Resources;
using Aspire.Cli.Utils;
using Semver;

namespace Aspire.Cli.Agents.AspireSkills;

/// <summary>
/// Creates and loads validated Aspire skills bundles.
/// </summary>
internal interface IAspireSkillsBundleProvider
{
    /// <summary>
    /// Creates an Aspire skills bundle from an archive and materializes its validated files
    /// in a dedicated staging directory.
    /// </summary>
    Task<AspireSkillsBundle> CreateAsync(
        FileInfo archive,
        DirectoryInfo bundleDirectory,
        string expectedArchiveSha512,
        CancellationToken cancellationToken,
        bool skipCompatibilityCheck = false);

    /// <summary>
    /// Loads an Aspire skills bundle from disk.
    /// </summary>
    Task<AspireSkillsBundle> LoadAsync(DirectoryInfo bundleDirectory, CancellationToken cancellationToken, bool skipCompatibilityCheck = false);
}

internal sealed class AspireSkillsBundleProvider : IAspireSkillsBundleProvider
{
    private const string ManifestFileName = "skill-manifest.json";
    private const string SkillsDirectoryName = "skills";
    private const string SkillFileName = "SKILL.md";
    private const int MaxSkillNameLength = 64;
    private const int MaxSkillDescriptionLength = 1024;

    private readonly string _currentCliVersion;
    private readonly string _currentSdkVersion;

    public AspireSkillsBundleProvider(CliExecutionContext executionContext)
    {
        ArgumentNullException.ThrowIfNull(executionContext);

        // The CLI and SDK share one resolved identity version. IdentitySdkVersion removes
        // build metadata so both values can be compared with manifest SemVer ranges.
        _currentCliVersion = executionContext.IdentitySdkVersion;
        _currentSdkVersion = executionContext.IdentitySdkVersion;
    }

    internal AspireSkillsBundleProvider()
        : this(VersionHelper.GetDefaultSdkVersion(), VersionHelper.GetDefaultSdkVersion())
    {
        // physical-binary-version-by-design (see docs/specs/cli-identity-sidecar.md):
        // this convenience constructor is only used by tests. Production resolves the
        // effective CLI identity through CliExecutionContext.
    }

    internal AspireSkillsBundleProvider(string currentCliVersion, string currentSdkVersion)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(currentCliVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(currentSdkVersion);

        _currentCliVersion = currentCliVersion;
        _currentSdkVersion = currentSdkVersion;
    }

    public async Task<AspireSkillsBundle> CreateAsync(
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
            ?? throw new InvalidOperationException("The Aspire skills bundle staging directory must have a parent directory.");
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

        var bundleRoot = FindBundleRoot(extractionDirectory.FullName);
        var bundle = await LoadAsync(bundleRoot, cancellationToken, skipCompatibilityCheck).ConfigureAwait(false);

        CopyDirectory(bundleRoot.FullName, bundleDirectory.FullName);
        return bundle;
    }

    public async Task<AspireSkillsBundle> LoadAsync(
        DirectoryInfo bundleDirectory,
        CancellationToken cancellationToken,
        bool skipCompatibilityCheck = false)
    {
        ArgumentNullException.ThrowIfNull(bundleDirectory);

        var manifestPath = Path.Combine(bundleDirectory.FullName, ManifestFileName);
        if (!File.Exists(manifestPath))
        {
            throw new InvalidOperationException(string.Format(CultureInfo.InvariantCulture, "Aspire skills bundle manifest was not found at '{0}'.", manifestPath));
        }

        SkillBundleManifest? manifest;
        try
        {
            await using var manifestStream = File.OpenRead(manifestPath);
            manifest = await JsonSerializer.DeserializeAsync(
                manifestStream,
                AspireSkillsJsonSerializerContext.Default.SkillBundleManifest,
                cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("Aspire skills bundle manifest is invalid.", ex);
        }

        if (manifest is null)
        {
            throw new InvalidOperationException("Aspire skills bundle manifest is empty or invalid.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        return CreateBundle(bundleDirectory, manifest, _currentCliVersion, _currentSdkVersion, skipCompatibilityCheck);
    }

    private static AspireSkillsBundle CreateBundle(
        DirectoryInfo bundleDirectory,
        SkillBundleManifest manifest,
        string currentCliVersion,
        string currentSdkVersion,
        bool skipCompatibilityCheck)
    {
        var version = manifest.Version;
        if (string.IsNullOrWhiteSpace(version))
        {
            throw new InvalidOperationException("Aspire skills bundle manifest must specify a version.");
        }

        // The bundle's `supports` range gates remotely acquired bundles, including cache
        // entries that another CLI version may have written. The exact snapshot embedded
        // in the current CLI may skip this check because its stamped range can lag the
        // binary version (e.g., a dogfood build of 13.5.x using a snapshot stamped
        // ">=13.4.0 <13.5.0").
        if (!skipCompatibilityCheck)
        {
            ValidateCompatibility(manifest.Supports, currentCliVersion, currentSdkVersion);
        }

        var skills = manifest.Skills;
        if (skills is not { Length: > 0 })
        {
            throw new InvalidOperationException("Aspire skills bundle manifest must contain at least one skill.");
        }

        var skillNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        List<ValidatedAspireSkill> validatedSkills = [];
        foreach (var skill in skills)
        {
            if (skill is null)
            {
                throw new InvalidOperationException("Aspire skills bundle manifest contains an empty skill entry.");
            }

            var skillName = skill.Name;
            if (string.IsNullOrWhiteSpace(skillName))
            {
                throw new InvalidOperationException("Aspire skills bundle manifest contains a skill without a name.");
            }

            ValidateSkillName(skillName);
            if (!skillNames.Add(skillName))
            {
                throw new InvalidOperationException(string.Format(CultureInfo.InvariantCulture, "Aspire skills bundle manifest contains duplicate skill '{0}'.", skillName));
            }

            if (string.IsNullOrWhiteSpace(skill.Description))
            {
                throw new InvalidOperationException(string.Format(CultureInfo.InvariantCulture, "Aspire skills bundle skill '{0}' must specify a description.", skillName));
            }

            var skillFiles = skill.Files;
            if (skillFiles is not { Length: > 0 })
            {
                throw new InvalidOperationException(string.Format(CultureInfo.InvariantCulture, "Aspire skills bundle skill '{0}' does not contain any files.", skillName));
            }

            var installExcludedRelativePaths = (skill.InstallExcludedRelativePaths ?? [])
                .Select(NormalizeRelativePath)
                .ToArray();
            if (installExcludedRelativePaths.Contains(SkillFileName, StringComparer.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(string.Format(CultureInfo.InvariantCulture, "Aspire skills bundle skill '{0}' cannot exclude SKILL.md from installation.", skillName));
            }

            var definition = SkillDefinition.CreateAspireSkillsBundle(
                skillName,
                skill.Description,
                installExcludedRelativePaths,
                skill.ApplicableLanguages ?? []);

            var filePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var hasSkillFile = false;
            List<SkillAssetFile> files = [];
            foreach (var file in skillFiles)
            {
                if (file is null)
                {
                    throw new InvalidOperationException(string.Format(CultureInfo.InvariantCulture, "Aspire skills bundle skill '{0}' contains an empty file entry.", skillName));
                }

                var validatedFile = ValidateFile(bundleDirectory, skillName, file);
                if (!filePaths.Add(validatedFile.RelativePath))
                {
                    throw new InvalidOperationException(string.Format(
                        CultureInfo.InvariantCulture,
                        "Aspire skills bundle skill '{0}' contains duplicate file '{1}'.",
                        skillName,
                        validatedFile.RelativePath));
                }

                files.Add(validatedFile);
                hasSkillFile |= string.Equals(validatedFile.RelativePath, SkillFileName, StringComparison.Ordinal);
            }

            if (!hasSkillFile)
            {
                throw new InvalidOperationException(string.Format(CultureInfo.InvariantCulture, "Aspire skills bundle skill '{0}' must contain SKILL.md.", skillName));
            }

            validatedSkills.Add(new ValidatedAspireSkill(definition, files));
        }

        return new AspireSkillsBundle(version, validatedSkills);
    }

    private static void ValidateSkillName(string skillName)
    {
        // Agent hosts use this grammar to discover skills consistently.
        // See https://agentskills.io/specification.
        if (skillName.Length > MaxSkillNameLength ||
            skillName[0] == '-' ||
            skillName[^1] == '-' ||
            skillName.Contains("--", StringComparison.Ordinal) ||
            skillName.Any(static character => !char.IsAsciiLetterLower(character) && !char.IsAsciiDigit(character) && character is not '-'))
        {
            throw new InvalidOperationException(string.Format(
                CultureInfo.InvariantCulture,
                "Aspire skills bundle skill name '{0}' must be 1-{1} characters, use only lowercase ASCII letters, digits, and hyphens, and must not start or end with a hyphen or contain consecutive hyphens.",
                skillName,
                MaxSkillNameLength));
        }
    }

    private static SkillAssetFile ValidateFile(DirectoryInfo bundleDirectory, string skillName, SkillBundleFile file)
    {
        var relativePath = NormalizeRelativePath(file.RelativePath);
        var fullPath = Path.Combine(bundleDirectory.FullName, SkillsDirectoryName, skillName, relativePath);
        if (!File.Exists(fullPath))
        {
            throw new InvalidOperationException(string.Format(CultureInfo.InvariantCulture, "Aspire skills bundle file '{0}' in skill '{1}' was not found.", relativePath, skillName));
        }

        // Hash and decode the same bytes so a concurrent filesystem change cannot
        // produce validated content that differs from the content retained in memory.
        var bytes = File.ReadAllBytes(fullPath);
        string expectedHash;
        string actualHash;
        string algorithmName;
        // The attestation-verified v0.0.1 archive predates the SHA-512 switch and cannot
        // be rebuilt without changing its signed subject digest. Prefer SHA-512 for current
        // bundles while continuing to validate that embedded archive's per-file SHA-256 hashes.
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
            throw new InvalidOperationException(string.Format(CultureInfo.InvariantCulture, "Aspire skills bundle file '{0}' in skill '{1}' does not specify a SHA-512 or SHA-256 hash.", relativePath, skillName));
        }

        if (!string.Equals(expectedHash, actualHash, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(string.Format(CultureInfo.InvariantCulture, "Aspire skills bundle file '{0}' in skill '{1}' failed {2} verification.", relativePath, skillName, algorithmName));
        }

        using var stream = new MemoryStream(bytes, writable: false);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var content = reader.ReadToEnd();
        if (string.Equals(relativePath, SkillFileName, StringComparison.Ordinal))
        {
            ValidateSkillFileFrontmatter(skillName, content);
        }

        return new SkillAssetFile(relativePath, content);
    }

    private static void ValidateSkillFileFrontmatter(string skillName, string content)
    {
        var frontmatterName = GetFrontmatterValue(content, "name");
        if (string.IsNullOrWhiteSpace(frontmatterName))
        {
            throw new InvalidOperationException(string.Format(CultureInfo.InvariantCulture, "Aspire skills bundle skill '{0}' must define a frontmatter name in SKILL.md.", skillName));
        }

        if (!string.Equals(frontmatterName, skillName, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(string.Format(
                CultureInfo.InvariantCulture,
                "Aspire skills bundle skill '{0}' SKILL.md frontmatter name '{1}' must match its manifest and directory name.",
                skillName,
                frontmatterName));
        }

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
        // Agent hosts read these fields directly, so validate the bundled SKILL.md
        // before caching content that they would reject or ignore.
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
        if (segments.Length == 0 || segments.Any(static segment => !IsPortablePathSegment(segment)))
        {
            throw new InvalidOperationException(string.Format(CultureInfo.InvariantCulture, "Aspire skills bundle path '{0}' is not safe.", relativePath));
        }

        return Path.Combine(segments);
    }

    private static bool IsPortablePathSegment(string segment)
    {
        // Bundle paths can be validated on one platform and installed on another. Reject the
        // Windows-invalid character set everywhere so ':' cannot create an NTFS alternate data
        // stream and other invalid filenames cannot enter a cached bundle.
        // See https://learn.microsoft.com/windows/win32/fileio/naming-a-file.
        return segment is not "." and not ".." &&
            !segment.Any(static character => char.IsControl(character) || character is '<' or '>' or ':' or '"' or '|' or '?' or '*');
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

    private static void ValidateArchiveSha512(string archivePath, string expectedSha512)
    {
        var expectedHash = NormalizeSha512(expectedSha512);
        using var stream = File.OpenRead(archivePath);
        var actualHash = Convert.ToHexString(SHA512.HashData(stream)).ToLowerInvariant();

        if (!string.Equals(expectedHash, actualHash, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(string.Format(
                CultureInfo.CurrentCulture,
                AgentCommandStrings.AspireSkillsInstaller_ArchiveHashVerificationFailed,
                expectedHash,
                actualHash));
        }
    }

    private static void ExtractArchive(string archivePath, string destinationDirectory)
    {
        if (archivePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            ExtractZipArchive(archivePath, destinationDirectory);
            return;
        }

        ExtractTarball(archivePath, destinationDirectory);
    }

    private static void ExtractTarball(string tarballPath, string destinationDirectory)
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
                    throw new InvalidDataException(string.Format(CultureInfo.InvariantCulture, "Aspire skills archive entry '{0}' has unsupported type '{1}'.", entry.Name, entry.EntryType));
            }
        }
    }

    private static void ExtractZipArchive(string archivePath, string destinationDirectory)
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

    private static string GetSafeArchiveDestinationPath(string destinationRoot, string entryName)
    {
        var normalizedEntryName = entryName.Replace('\\', '/');
        var segments = normalizedEntryName.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (Path.IsPathRooted(normalizedEntryName) ||
            segments.Length == 0 ||
            segments.Any(static segment => !IsPortablePathSegment(segment)))
        {
            throw new InvalidDataException(string.Format(CultureInfo.InvariantCulture, "Aspire skills archive entry '{0}' is not safe.", entryName));
        }

        var destinationPath = Path.GetFullPath(Path.Combine(destinationRoot, normalizedEntryName.Replace('/', Path.DirectorySeparatorChar)));
        if (!destinationPath.StartsWith(destinationRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal) &&
            !string.Equals(destinationPath, destinationRoot, StringComparison.Ordinal))
        {
            throw new InvalidDataException(string.Format(CultureInfo.InvariantCulture, "Aspire skills archive entry '{0}' escapes the extraction directory.", entryName));
        }

        return destinationPath;
    }

    private static DirectoryInfo FindBundleRoot(string extractionDirectory)
    {
        var rootManifestPath = Path.Combine(extractionDirectory, ManifestFileName);
        if (File.Exists(rootManifestPath))
        {
            return new DirectoryInfo(extractionDirectory);
        }

        var packageDirectory = Path.Combine(extractionDirectory, "package");
        var packageManifestPath = Path.Combine(packageDirectory, ManifestFileName);
        if (File.Exists(packageManifestPath))
        {
            return new DirectoryInfo(packageDirectory);
        }

        var topLevelBundleDirectories = Directory
            .EnumerateDirectories(extractionDirectory)
            .Where(directory => File.Exists(Path.Combine(directory, ManifestFileName)))
            .ToArray();

        if (topLevelBundleDirectories.Length == 1)
        {
            return new DirectoryInfo(topLevelBundleDirectories[0]);
        }

        if (topLevelBundleDirectories.Length > 1)
        {
            throw new InvalidOperationException("Downloaded Aspire skills package contains multiple skill-manifest.json files.");
        }

        throw new InvalidOperationException("Downloaded Aspire skills package does not contain skill-manifest.json.");
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
