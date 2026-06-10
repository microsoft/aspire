// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using System.Xml.Linq;
using Microsoft.Extensions.Logging;

namespace Aspire.Cli.Projects;

/// <summary>
/// Shared MSBuild-side closure contract used by both the polyglot
/// <see cref="PrebuiltAppHostServer"/> and the CLI-managed
/// <see cref="IntegrationClosureRestorer"/>. Owns the file-name constants,
/// the project-file XML emission (properties + AfterBuild targets), and the
/// post-build closure file reader.
/// </summary>
/// <remarks>
/// The MSBuild contract is intentionally narrow: a small set of <c>AspireClosure*File</c>
/// path properties tell the project where to write the closure files, and two
/// <c>AfterTargets=Build</c> targets do the writing. Both consumers must speak the same
/// contract or one of them will silently miss entries — keeping the emission and reading
/// code centralized prevents that drift.
/// </remarks>
internal static class IntegrationClosureBuilder
{
    internal const string ClosureMetadataFileName = "closure-metadata.txt";
    internal const string ClosureSourcesFileName = "closure-sources.txt";
    internal const string ClosureTargetsFileName = "closure-targets.txt";
    internal const string ProjectRefAssemblyNamesFileName = "project-ref-assemblies.txt";
    internal const string IntegrationRestoreFolderName = "integration-restore";
    internal const string ProjectAssetsFileName = "project.assets.json";

    /// <summary>
    /// Adds the <c>AspireClosure*File</c> path properties pointing at the closure files this
    /// restorer reads after build, plus <c>CopyLocalLockFileAssemblies=true</c> so the closure
    /// captures the transitive package DLL set, and <c>ProduceReferenceAssembly=false</c> so
    /// reference-assembly outputs don't pollute the closure.
    /// </summary>
    public static void AddClosureProperties(XElement propertyGroup, string restoreDir)
    {
        ArgumentNullException.ThrowIfNull(propertyGroup);
        ArgumentException.ThrowIfNullOrEmpty(restoreDir);

        propertyGroup.Add(
            new XElement("CopyLocalLockFileAssemblies", "true"),
            new XElement("ProduceReferenceAssembly", "false"),
            new XElement("AspireClosureMetadataFile", Path.Combine(restoreDir, ClosureMetadataFileName)),
            new XElement("AspireClosureSourcesFile", Path.Combine(restoreDir, ClosureSourcesFileName)),
            new XElement("AspireClosureTargetsFile", Path.Combine(restoreDir, ClosureTargetsFileName)),
            new XElement("AspireProjectRefAssemblyNamesFile", Path.Combine(restoreDir, ProjectRefAssemblyNamesFileName)));
    }

    /// <summary>
    /// Adds the <c>_WriteAspireClosureManifest</c> and <c>_WriteAspireProjectRefAssemblyNames</c>
    /// MSBuild targets to the project. These run <c>AfterTargets=Build</c> and emit the closure
    /// files <see cref="ReadClosureManifestAsync"/> then post-processes.
    /// </summary>
    /// <remarks>
    /// The targets are conditioned on the <c>AspireClosure*File</c> properties being set so they
    /// stay inert when imported into a project that doesn't opt in (e.g. the file-based AppHost
    /// being run via <c>dotnet run apphost.cs</c> with no CLI involvement).
    /// </remarks>
    public static void AddClosureTargets(XElement projectRoot)
    {
        ArgumentNullException.ThrowIfNull(projectRoot);

        projectRoot.Add(
            new XElement("Target",
                new XAttribute("Name", "_WriteAspireProjectRefAssemblyNames"),
                new XAttribute("AfterTargets", "Build"),
                new XAttribute("Condition", "'$(AspireProjectRefAssemblyNamesFile)' != ''"),
                new XElement("WriteLinesToFile",
                    new XAttribute("File", "$(AspireProjectRefAssemblyNamesFile)"),
                    new XAttribute("Lines", "@(_ResolvedProjectReferencePaths->'%(Filename)')"),
                    new XAttribute("Overwrite", "true"),
                    new XAttribute("WriteOnlyWhenDifferent", "true"))));

        projectRoot.Add(
            new XElement("Target",
                new XAttribute("Name", "_WriteAspireClosureManifest"),
                new XAttribute("AfterTargets", "Build"),
                new XAttribute("Condition", "'$(AspireClosureSourcesFile)' != ''"),
                new XAttribute("DependsOnTargets", "ResolveLockFileCopyLocalFiles"),
                new XElement("WriteLinesToFile",
                    new XAttribute("File", "$(AspireClosureSourcesFile)"),
                    new XAttribute("Lines", "@(ReferenceCopyLocalPaths->'%(FullPath)')"),
                    new XAttribute("Overwrite", "true"),
                    new XAttribute("WriteOnlyWhenDifferent", "true")),
                new XElement("WriteLinesToFile",
                    new XAttribute("File", "$(AspireClosureMetadataFile)"),
                    new XAttribute("Lines", "@(ReferenceCopyLocalPaths->'%(NuGetPackageId)|%(NuGetPackageVersion)|%(PathInPackage)|%(AssetType)')"),
                    new XAttribute("Overwrite", "true"),
                    new XAttribute("WriteOnlyWhenDifferent", "true")),
                new XElement("WriteLinesToFile",
                    new XAttribute("File", "$(AspireClosureTargetsFile)"),
                    new XAttribute("Lines", "@(ReferenceCopyLocalPaths->'%(DestinationSubDirectory)%(Filename)%(Extension)')"),
                    new XAttribute("Overwrite", "true"),
                    new XAttribute("WriteOnlyWhenDifferent", "true"))));
    }

    /// <summary>
    /// Reads the closure files MSBuild emitted under <paramref name="restoreDir"/>, joins them
    /// with NuGet package fingerprints from <paramref name="assetsFilePath"/>, and constructs an
    /// <see cref="AppHostServerClosureManifest"/>.
    /// </summary>
    /// <param name="restoreDir">Directory containing <c>closure-sources.txt</c>, <c>closure-metadata.txt</c>, <c>closure-targets.txt</c>, and optionally <c>project-ref-assemblies.txt</c>.</param>
    /// <param name="assetsFilePath">Absolute path to NuGet's <c>project.assets.json</c> for the restore project. Used to fingerprint package-backed entries.</param>
    /// <param name="appSettingsContent">Content used as the manifest's <c>appsettings.json</c> hash input. Both consumers contribute their own variant.</param>
    /// <param name="missingFileBehavior">Controls how missing closure files are handled.</param>
    /// <param name="logger">Optional logger for diagnostics.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The materialized manifest, or <c>null</c> when <see cref="ClosureFileMissingBehavior.ReturnNull"/> is used and a required file is absent.</returns>
    public static async Task<AppHostServerClosureManifest?> ReadClosureManifestAsync(
        string restoreDir,
        string assetsFilePath,
        string appSettingsContent,
        ClosureFileMissingBehavior missingFileBehavior,
        ILogger? logger,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(restoreDir);
        ArgumentException.ThrowIfNullOrEmpty(assetsFilePath);
        ArgumentNullException.ThrowIfNull(appSettingsContent);

        var sourcesPath = Path.Combine(restoreDir, ClosureSourcesFileName);
        var metadataPath = Path.Combine(restoreDir, ClosureMetadataFileName);
        var targetsPath = Path.Combine(restoreDir, ClosureTargetsFileName);

        if (!File.Exists(sourcesPath) || !File.Exists(metadataPath) || !File.Exists(targetsPath))
        {
            var message = $"Integration closure manifest files were not produced under '{restoreDir}'. The integration project may not have built successfully.";
            if (missingFileBehavior == ClosureFileMissingBehavior.Throw)
            {
                throw new InvalidOperationException(message);
            }

            logger?.LogWarning("{Message}", message);
            return null;
        }

        var sourcePaths = await ReadManifestLinesAsync(sourcesPath, cancellationToken).ConfigureAwait(false);
        var metadataLines = await ReadManifestLinesAsync(metadataPath, cancellationToken).ConfigureAwait(false);
        var targetPaths = await ReadManifestLinesAsync(targetsPath, cancellationToken).ConfigureAwait(false);

        if (sourcePaths.Count != metadataLines.Count || sourcePaths.Count != targetPaths.Count)
        {
            throw new InvalidOperationException(
                $"Integration closure manifest is inconsistent. Sources: {sourcePaths.Count}, metadata: {metadataLines.Count}, targets: {targetPaths.Count}.");
        }

        // project-ref-assemblies.txt is read separately by callers that need it for their own
        // appsettings derivation (the closure manifest itself doesn't reference these names —
        // they flow into the appsettings content hash that callers pass in).

        // Fingerprints reference NuGet's content-addressed package id+version+sha512 triple so
        // the closure manifest can detect package-cache drift independently of file timestamps.
        var packageFingerprints = await ReadPackageFingerprintsAsync(
            assetsFilePath,
            missingFileBehavior,
            cancellationToken).ConfigureAwait(false);

        var entries = new List<AppHostServerClosureSource>(sourcePaths.Count);
        for (var i = 0; i < sourcePaths.Count; i++)
        {
            var metadata = ParseClosureMetadata(metadataLines[i]);
            var packageSha512 = TryGetPackageFingerprint(packageFingerprints, metadata);
            entries.Add(new AppHostServerClosureSource(
                sourcePaths[i],
                targetPaths[i],
                metadata.NuGetPackageId,
                metadata.NuGetPackageVersion,
                metadata.PathInPackage,
                packageSha512,
                metadata.AssetType));
        }

        return AppHostServerClosureManifest.Create(entries, appSettingsContent, cancellationToken);
    }

    /// <summary>
    /// Reads <c>project-ref-assemblies.txt</c> (the assembly names contributed by project
    /// references). Returns an empty list when the file is absent.
    /// </summary>
    public static async Task<List<string>> ReadProjectRefAssemblyNamesAsync(string restoreDir, ILogger? logger, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(restoreDir);

        var path = Path.Combine(restoreDir, ProjectRefAssemblyNamesFileName);
        if (!File.Exists(path))
        {
            logger?.LogWarning("Project reference assembly names file not found at {Path}", path);
            return [];
        }

        return await ReadManifestLinesAsync(path, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Parses one line of <c>closure-metadata.txt</c>. The MSBuild emitter writes:
    /// <code>NuGetPackageId|NuGetPackageVersion|PathInPackage|AssetType</code>
    /// Empty segments are legal — project-ref entries have no package id/version/path.
    /// </summary>
    internal static ClosureMetadata ParseClosureMetadata(string line)
    {
        ArgumentNullException.ThrowIfNull(line);

        var parts = line.Split('|', 4);
        if (parts.Length != 4)
        {
            throw new InvalidOperationException($"Integration closure metadata line '{line}' is invalid.");
        }

        return new ClosureMetadata(
            NormalizeMetadataValue(parts[0]),
            NormalizeMetadataValue(parts[1]),
            NormalizeMetadataValue(parts[2]),
            NormalizeMetadataValue(parts[3]));

        static string? NormalizeMetadataValue(string value)
            => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    internal static string? TryGetPackageFingerprint(IReadOnlyDictionary<string, string> fingerprints, ClosureMetadata metadata)
    {
        if (metadata.NuGetPackageId is null ||
            metadata.NuGetPackageVersion is null ||
            metadata.PathInPackage is null)
        {
            return null;
        }

        return fingerprints.TryGetValue(
            CreatePackageFingerprintKey(metadata.NuGetPackageId, metadata.NuGetPackageVersion),
            out var sha512)
            ? sha512
            : null;
    }

    private static string CreatePackageFingerprintKey(string packageId, string packageVersion)
        => $"{packageId}/{packageVersion}";

    private static async Task<List<string>> ReadManifestLinesAsync(string filePath, CancellationToken cancellationToken)
    {
        var lines = await File.ReadAllLinesAsync(filePath, cancellationToken).ConfigureAwait(false);
        return lines.Where(static l => !string.IsNullOrWhiteSpace(l)).Select(static l => l.Trim()).ToList();
    }

    private static async Task<Dictionary<string, string>> ReadPackageFingerprintsAsync(
        string assetsFilePath,
        ClosureFileMissingBehavior missingFileBehavior,
        CancellationToken cancellationToken)
    {
        var fingerprints = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(assetsFilePath))
        {
            if (missingFileBehavior == ClosureFileMissingBehavior.Throw)
            {
                throw new InvalidOperationException($"Integration assets file '{assetsFilePath}' was not found after build.");
            }

            return fingerprints;
        }

        await using var stream = File.OpenRead(assetsFilePath);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);

        if (!document.RootElement.TryGetProperty("libraries", out var libraries))
        {
            return fingerprints;
        }

        // project.assets.json shape (excerpt):
        //   "libraries": {
        //     "Aspire.Hosting.Redis/13.2.1": { "type": "package", "sha512": "sha512-...", ... },
        //     "ProjectName/1.0.0":           { "type": "project", ... }
        //   }
        // Only "package" entries carry a sha512 — project entries are skipped.
        foreach (var library in libraries.EnumerateObject())
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!library.Value.TryGetProperty("type", out var typeElement) ||
                !string.Equals(typeElement.GetString(), "package", StringComparison.OrdinalIgnoreCase) ||
                !library.Value.TryGetProperty("sha512", out var sha512Element))
            {
                continue;
            }

            var sha512 = sha512Element.GetString();
            if (string.IsNullOrWhiteSpace(sha512))
            {
                continue;
            }

            var separatorIndex = library.Name.IndexOf('/');
            if (separatorIndex <= 0 || separatorIndex == library.Name.Length - 1)
            {
                continue;
            }

            var packageId = library.Name[..separatorIndex];
            var packageVersion = library.Name[(separatorIndex + 1)..];
            fingerprints[CreatePackageFingerprintKey(packageId, packageVersion)] = sha512;
        }

        return fingerprints;
    }

    internal readonly record struct ClosureMetadata(
        string? NuGetPackageId,
        string? NuGetPackageVersion,
        string? PathInPackage,
        string? AssetType);
}

/// <summary>
/// Controls how <see cref="IntegrationClosureBuilder.ReadClosureManifestAsync"/> reacts to
/// missing input files. The polyglot prebuilt server treats missing files as hard errors
/// (the build is expected to produce them); the CLI-managed restorer warns and returns
/// <c>null</c> so the caller can surface the failure with its own diagnostics.
/// </summary>
internal enum ClosureFileMissingBehavior
{
    Throw,
    ReturnNull,
}
