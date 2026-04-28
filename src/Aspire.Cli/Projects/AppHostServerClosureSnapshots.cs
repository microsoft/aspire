// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.IO.Hashing;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Aspire.Cli.Projects;

/// <summary>
/// Represents an immutable snapshot of the prebuilt AppHost server closure.
/// </summary>
internal sealed class AppHostServerClosureSnapshot
{
    public const string IntegrationPackageProbeManifestFileName = "integration-package-probe-manifest.json";

    public required string SnapshotPath { get; init; }

    public required string ManifestFingerprint { get; init; }

    public string ContentRootPath => Path.Combine(SnapshotPath, "content");

    public string IntegrationLibsPath => Path.Combine(SnapshotPath, "libs");

    public string IntegrationPackageProbeManifestPath => Path.Combine(ContentRootPath, IntegrationPackageProbeManifestFileName);
}

/// <summary>
/// Represents a resolved file in the prebuilt AppHost server closure.
/// </summary>
/// <param name="SourcePath">The full path of the source file used to materialize the snapshot.</param>
/// <param name="RelativePath">The file path relative to the snapshot libs directory.</param>
/// <param name="PackageId">The NuGet package id when the source comes from a restored package.</param>
/// <param name="PackageVersion">The NuGet package version when the source comes from a restored package.</param>
/// <param name="PathInPackage">The relative path inside the NuGet package when the source comes from a restored package.</param>
/// <param name="PackageSha512">The package content hash from <c>project.assets.json</c> when available.</param>
/// <param name="AssetType">The resolved NuGet asset type when the source comes from a restored package.</param>
internal sealed record AppHostServerClosureSource(
    string SourcePath,
    string RelativePath,
    string? PackageId = null,
    string? PackageVersion = null,
    string? PathInPackage = null,
    string? PackageSha512 = null,
    string? AssetType = null);

/// <summary>
/// Represents a single file in the prebuilt AppHost server closure manifest.
/// </summary>
internal sealed class AppHostServerClosureManifestEntry
{
    public required string RelativePath { get; init; }

    public required string SourcePath { get; init; }

    public string? PackageId { get; init; }

    public string? PackageVersion { get; init; }

    public string? PathInPackage { get; init; }

    public string? PackageSha512 { get; init; }

    public string? AssetType { get; init; }

    public long? FileLength { get; init; }

    public long? LastWriteTimeUtcTicks { get; init; }

    public bool IsPackageBacked =>
        !string.IsNullOrWhiteSpace(PackageId) &&
        !string.IsNullOrWhiteSpace(PackageVersion) &&
        !string.IsNullOrWhiteSpace(PathInPackage) &&
        !string.IsNullOrWhiteSpace(PackageSha512);
}

/// <summary>
/// Represents the exact closure used to materialize a prebuilt AppHost server snapshot.
/// </summary>
internal sealed class AppHostServerClosureManifest
{
    public required string ManifestFingerprint { get; init; }

    public required IReadOnlyList<AppHostServerClosureManifestEntry> Entries { get; init; }

    public required string AppSettingsContent { get; init; }

    internal IReadOnlyList<string> GetManifestLines()
    {
        var lines = new List<string>(Entries.Count + 1)
        {
            $"content/appsettings.json|{ComputeTextHash(AppSettingsContent)}"
        };

        lines.AddRange(Entries.Select(GetEntryFingerprint));

        return lines;
    }

    public static AppHostServerClosureManifest Create(
        IEnumerable<AppHostServerClosureSource> sourceFiles,
        string appSettingsContent,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sourceFiles);
        ArgumentNullException.ThrowIfNull(appSettingsContent);

        var entries = new List<AppHostServerClosureManifestEntry>();
        foreach (var sourceFile in sourceFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var normalizedSourcePath = Path.GetFullPath(sourceFile.SourcePath);
            var normalizedRelativePath = NormalizeRelativePath(sourceFile.RelativePath);
            if (!File.Exists(normalizedSourcePath))
            {
                throw new InvalidOperationException($"Manifest source file '{normalizedSourcePath}' does not exist.");
            }

            if (TryCreatePackageBackedEntry(sourceFile, normalizedSourcePath, normalizedRelativePath) is { } packageEntry)
            {
                entries.Add(packageEntry);
                continue;
            }

            var fileInfo = new FileInfo(normalizedSourcePath);
            entries.Add(new AppHostServerClosureManifestEntry
            {
                RelativePath = normalizedRelativePath,
                SourcePath = normalizedSourcePath,
                FileLength = fileInfo.Length,
                LastWriteTimeUtcTicks = fileInfo.LastWriteTimeUtc.Ticks
            });
        }

        entries.Sort(static (left, right) => StringComparer.Ordinal.Compare(left.RelativePath, right.RelativePath));

        return new AppHostServerClosureManifest
        {
            ManifestFingerprint = ComputeManifestFingerprint(entries, appSettingsContent),
            Entries = entries,
            AppSettingsContent = appSettingsContent
        };
    }

    private static string ComputeManifestFingerprint(
        IReadOnlyList<AppHostServerClosureManifestEntry> entries,
        string appSettingsContent)
    {
        var values = new List<string>(entries.Count + 1)
        {
            $"content/appsettings.json|{ComputeTextHash(appSettingsContent)}"
        };

        values.AddRange(entries.Select(GetEntryFingerprint));
        return ComputeHash(values);
    }

    private static AppHostServerClosureManifestEntry? TryCreatePackageBackedEntry(
        AppHostServerClosureSource sourceFile,
        string normalizedSourcePath,
        string normalizedRelativePath)
    {
        if (string.IsNullOrWhiteSpace(sourceFile.PackageId) ||
            string.IsNullOrWhiteSpace(sourceFile.PackageVersion) ||
            string.IsNullOrWhiteSpace(sourceFile.PathInPackage) ||
            string.IsNullOrWhiteSpace(sourceFile.PackageSha512))
        {
            return null;
        }

        return new AppHostServerClosureManifestEntry
        {
            RelativePath = normalizedRelativePath,
            SourcePath = normalizedSourcePath,
            PackageId = sourceFile.PackageId,
            PackageVersion = sourceFile.PackageVersion,
            PathInPackage = sourceFile.PathInPackage,
            PackageSha512 = sourceFile.PackageSha512,
            AssetType = NormalizeAssetType(sourceFile.AssetType)
        };
    }

    private static string GetEntryFingerprint(AppHostServerClosureManifestEntry entry)
    {
        if (entry.IsPackageBacked)
        {
            return $"packages/{entry.RelativePath}|{entry.AssetType ?? "runtime"}|{entry.PackageId}|{entry.PackageVersion}|{entry.PathInPackage}|{entry.PackageSha512}|{entry.SourcePath}";
        }

        if (entry.FileLength is long fileLength && entry.LastWriteTimeUtcTicks is long lastWriteTimeUtcTicks)
        {
            return $"libs/{entry.RelativePath}|file|{fileLength}|{lastWriteTimeUtcTicks}";
        }

        throw new InvalidOperationException($"Manifest entry '{entry.RelativePath}' does not contain enough information to compute a fingerprint.");
    }

    private static string ComputeTextHash(string value)
    {
        var hash = new XxHash3();
        hash.Append(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(hash.GetCurrentHash()).ToLowerInvariant();
    }

    private static string ComputeHash(IEnumerable<string> values)
    {
        var hash = new XxHash3();
        foreach (var value in values)
        {
            hash.Append(Encoding.UTF8.GetBytes(value));
            hash.Append("\n"u8);
        }

        return Convert.ToHexString(hash.GetCurrentHash()).ToLowerInvariant();
    }

    private static string? NormalizeAssetType(string? assetType)
    {
        return string.IsNullOrWhiteSpace(assetType) ? null : assetType.Trim();
    }

    private static string NormalizeRelativePath(string relativePath)
    {
        ArgumentException.ThrowIfNullOrEmpty(relativePath);

        return relativePath
            .Replace('\\', '/')
            .TrimStart('/');
    }
}

/// <summary>
/// Represents the runtime probe manifest for package-backed AppHost closure assets.
/// </summary>
internal sealed class AppHostServerPackageProbeManifest
{
    public required IReadOnlyList<AppHostServerPackageManagedAssembly> ManagedAssemblies { get; init; }

    public required IReadOnlyList<AppHostServerPackageNativeLibrary> NativeLibraries { get; init; }

    public static AppHostServerPackageProbeManifest Create(IReadOnlyList<AppHostServerClosureManifestEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        var managedAssemblies = new List<AppHostServerPackageManagedAssembly>();
        var nativeLibraries = new List<AppHostServerPackageNativeLibrary>();

        foreach (var entry in entries.Where(static entry => entry.IsPackageBacked))
        {
            if (string.Equals(entry.AssetType, "native", StringComparison.OrdinalIgnoreCase))
            {
                nativeLibraries.Add(new AppHostServerPackageNativeLibrary
                {
                    FileName = Path.GetFileName(entry.RelativePath),
                    Path = entry.SourcePath
                });
                continue;
            }

            if (!entry.SourcePath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            managedAssemblies.Add(new AppHostServerPackageManagedAssembly
            {
                Name = Path.GetFileNameWithoutExtension(entry.RelativePath),
                Culture = TryGetSatelliteCulture(entry),
                Path = entry.SourcePath
            });
        }

        managedAssemblies.Sort(static (left, right) =>
        {
            var nameComparison = StringComparer.OrdinalIgnoreCase.Compare(left.Name, right.Name);
            if (nameComparison != 0)
            {
                return nameComparison;
            }

            var cultureComparison = StringComparer.OrdinalIgnoreCase.Compare(left.Culture, right.Culture);
            return cultureComparison != 0
                ? cultureComparison
                : StringComparer.Ordinal.Compare(left.Path, right.Path);
        });

        nativeLibraries.Sort(static (left, right) =>
        {
            var nameComparison = StringComparer.OrdinalIgnoreCase.Compare(left.FileName, right.FileName);
            return nameComparison != 0
                ? nameComparison
                : StringComparer.Ordinal.Compare(left.Path, right.Path);
        });

        return new AppHostServerPackageProbeManifest
        {
            ManagedAssemblies = managedAssemblies,
            NativeLibraries = nativeLibraries
        };
    }

    private static string? TryGetSatelliteCulture(AppHostServerClosureManifestEntry entry)
    {
        if (!string.Equals(entry.AssetType, "resources", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var directoryName = Path.GetDirectoryName(entry.RelativePath.Replace('/', Path.DirectorySeparatorChar));
        if (string.IsNullOrWhiteSpace(directoryName))
        {
            return null;
        }

        return directoryName.Replace('\\', '/').Trim('/');
    }
}

/// <summary>
/// Represents a package-backed managed assembly that should be loaded from the package cache.
/// </summary>
internal sealed class AppHostServerPackageManagedAssembly
{
    public required string Name { get; init; }

    public string? Culture { get; init; }

    public required string Path { get; init; }
}

/// <summary>
/// Represents a package-backed native library that should be loaded from the package cache.
/// </summary>
internal sealed class AppHostServerPackageNativeLibrary
{
    public required string FileName { get; init; }

    public required string Path { get; init; }
}

/// <summary>
/// Stores immutable snapshots of the prebuilt AppHost server closure.
/// </summary>
internal sealed class AppHostServerClosureSnapshotStore
{
    private const string SnapshotItemsFolderName = "items";
    private const string SnapshotManifestFileName = "manifest.txt";
    private const string SnapshotRootFolderName = "snapshots";
    private const string SnapshotStagingFolderName = ".staging";

    private static readonly TimeSpan s_stagingCleanupAge = TimeSpan.FromHours(1);

    private readonly string _itemsDirectory;
    private readonly ILogger _logger;
    private readonly string _stagingDirectory;

    public AppHostServerClosureSnapshotStore(string rootPath, ILogger logger)
    {
        ArgumentException.ThrowIfNullOrEmpty(rootPath);

        var snapshotsDirectory = Path.Combine(Path.GetFullPath(rootPath), SnapshotRootFolderName);
        _itemsDirectory = Path.Combine(snapshotsDirectory, SnapshotItemsFolderName);
        _stagingDirectory = Path.Combine(snapshotsDirectory, SnapshotStagingFolderName);
        _logger = logger;

        Directory.CreateDirectory(_itemsDirectory);
        Directory.CreateDirectory(_stagingDirectory);
    }

    public void CleanupStagingDirectories()
    {
        if (!Directory.Exists(_stagingDirectory))
        {
            return;
        }

        foreach (var stagingDirectory in Directory.EnumerateDirectories(_stagingDirectory))
        {
            try
            {
                var directoryInfo = new DirectoryInfo(stagingDirectory);
                if (DateTime.UtcNow - directoryInfo.LastWriteTimeUtc <= s_stagingCleanupAge)
                {
                    continue;
                }

                Directory.Delete(stagingDirectory, recursive: true);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to clean stale AppHost snapshot staging directory {Path}", stagingDirectory);
            }
        }
    }

    public AppHostServerClosureSnapshot? TryLoadSnapshot(string manifestFingerprint)
    {
        ArgumentException.ThrowIfNullOrEmpty(manifestFingerprint);

        var snapshotPath = GetSnapshotPath(manifestFingerprint);
        var contentRootPath = Path.Combine(snapshotPath, "content");
        var libsPath = Path.Combine(snapshotPath, "libs");
        var appSettingsPath = Path.Combine(contentRootPath, "appsettings.json");
        var probeManifestPath = Path.Combine(contentRootPath, AppHostServerClosureSnapshot.IntegrationPackageProbeManifestFileName);

        if (!Directory.Exists(snapshotPath) ||
            !Directory.Exists(contentRootPath) ||
            !Directory.Exists(libsPath) ||
            !File.Exists(appSettingsPath) ||
            !File.Exists(probeManifestPath))
        {
            return null;
        }

        return new AppHostServerClosureSnapshot
        {
            SnapshotPath = snapshotPath,
            ManifestFingerprint = manifestFingerprint
        };
    }

    public async Task<AppHostServerClosureSnapshot> CreateAsync(
        AppHostServerClosureManifest manifest,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        if (TryLoadSnapshot(manifest.ManifestFingerprint) is { } existingSnapshot)
        {
            return existingSnapshot;
        }

        var stagingPath = Path.Combine(_stagingDirectory, Guid.NewGuid().ToString("n"));
        var libsPath = Path.Combine(stagingPath, "libs");
        var contentRootPath = Path.Combine(stagingPath, "content");
        var packageProbeManifest = AppHostServerPackageProbeManifest.Create(manifest.Entries);

        Directory.CreateDirectory(libsPath);
        Directory.CreateDirectory(contentRootPath);

        try
        {
            foreach (var entry in manifest.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (entry.IsPackageBacked)
                {
                    continue;
                }

                var destinationPath = Path.Combine(libsPath, entry.RelativePath.Replace('/', Path.DirectorySeparatorChar));
                var destinationDirectory = Path.GetDirectoryName(destinationPath);
                if (!string.IsNullOrEmpty(destinationDirectory))
                {
                    Directory.CreateDirectory(destinationDirectory);
                }

                File.Copy(entry.SourcePath, destinationPath, overwrite: false);
            }

            await File.WriteAllTextAsync(
                Path.Combine(contentRootPath, "appsettings.json"),
                manifest.AppSettingsContent,
                cancellationToken).ConfigureAwait(false);

            await WritePackageProbeManifestAsync(
                Path.Combine(contentRootPath, AppHostServerClosureSnapshot.IntegrationPackageProbeManifestFileName),
                packageProbeManifest,
                cancellationToken).ConfigureAwait(false);

            await WriteManifestFileAsync(Path.Combine(stagingPath, SnapshotManifestFileName), manifest, cancellationToken).ConfigureAwait(false);

            var finalSnapshotPath = GetSnapshotPath(manifest.ManifestFingerprint);
            Directory.Move(stagingPath, finalSnapshotPath);

            _logger.LogInformation("Created AppHost closure snapshot {SnapshotFingerprint}", manifest.ManifestFingerprint);

            return new AppHostServerClosureSnapshot
            {
                SnapshotPath = finalSnapshotPath,
                ManifestFingerprint = manifest.ManifestFingerprint
            };
        }
        catch
        {
            TryDeleteDirectory(stagingPath);
            throw;
        }
    }

    private string GetSnapshotPath(string manifestFingerprint)
    {
        return Path.Combine(_itemsDirectory, manifestFingerprint);
    }

    private static async Task WriteManifestFileAsync(
        string path,
        AppHostServerClosureManifest manifest,
        CancellationToken cancellationToken)
    {
        await File.WriteAllLinesAsync(path, manifest.GetManifestLines(), cancellationToken).ConfigureAwait(false);
    }

    private static Task WritePackageProbeManifestAsync(
        string path,
        AppHostServerPackageProbeManifest manifest,
        CancellationToken cancellationToken)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WritePropertyName("managedAssemblies");
            writer.WriteStartArray();
            foreach (var managedAssembly in manifest.ManagedAssemblies)
            {
                writer.WriteStartObject();
                writer.WriteString("name", managedAssembly.Name);
                if (managedAssembly.Culture is not null)
                {
                    writer.WriteString("culture", managedAssembly.Culture);
                }
                writer.WriteString("path", managedAssembly.Path);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();

            writer.WritePropertyName("nativeLibraries");
            writer.WriteStartArray();
            foreach (var nativeLibrary in manifest.NativeLibraries)
            {
                writer.WriteStartObject();
                writer.WriteString("fileName", nativeLibrary.FileName);
                writer.WriteString("path", nativeLibrary.Path);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return File.WriteAllBytesAsync(path, stream.ToArray(), cancellationToken);
    }

    private void TryDeleteDirectory(string path)
    {
        if (!Directory.Exists(path))
        {
            return;
        }

        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to delete AppHost snapshot directory {Path}", path);
        }
    }
}
