// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.IO.Hashing;
using System.Text;
using Aspire.Hosting;
using Microsoft.Extensions.Logging;

namespace Aspire.Cli.Projects;

/// <summary>
/// Represents an immutable project-reference output layout for the prebuilt AppHost server.
/// </summary>
internal sealed class AppHostServerProjectLayout
{
    public required string LayoutPath { get; init; }

    public required string Fingerprint { get; init; }

    public string IntegrationLibsPath => Path.Combine(LayoutPath, "libs");
}

/// <summary>
/// Represents a resolved file in the prebuilt AppHost server closure.
/// </summary>
/// <param name="SourcePath">The full path of the source file used to materialize the closure.</param>
/// <param name="RelativePath">The file path relative to the integration libs directory.</param>
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

    public string? FileContentHash { get; init; }

    public bool IsPackageBacked =>
        !string.IsNullOrWhiteSpace(PackageId) &&
        !string.IsNullOrWhiteSpace(PackageVersion) &&
        !string.IsNullOrWhiteSpace(PathInPackage) &&
        !string.IsNullOrWhiteSpace(PackageSha512);
}

/// <summary>
/// Represents the exact closure used to prepare a prebuilt AppHost server.
/// </summary>
internal sealed class AppHostServerClosureManifest
{
    public required string ManifestFingerprint { get; init; }

    public required string? ProjectLayoutFingerprint { get; init; }

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

    internal IReadOnlyList<string> GetProjectLayoutManifestLines()
    {
        return Entries
            .Where(static entry => !entry.IsPackageBacked)
            .Select(GetProjectEntryFingerprint)
            .ToList();
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

            entries.Add(new AppHostServerClosureManifestEntry
            {
                RelativePath = normalizedRelativePath,
                SourcePath = normalizedSourcePath,
                FileContentHash = ComputeFileHash(normalizedSourcePath, cancellationToken)
            });
        }

        entries.Sort(static (left, right) => StringComparer.Ordinal.Compare(left.RelativePath, right.RelativePath));

        return new AppHostServerClosureManifest
        {
            ManifestFingerprint = ComputeManifestFingerprint(entries, appSettingsContent),
            ProjectLayoutFingerprint = ComputeProjectLayoutFingerprint(entries),
            Entries = entries,
            AppSettingsContent = appSettingsContent
        };
    }

    public IntegrationPackageProbeManifest CreatePackageProbeManifest()
    {
        var managedAssemblies = new List<IntegrationPackageManagedAssembly>();
        var nativeLibraries = new List<IntegrationPackageNativeLibrary>();

        foreach (var entry in Entries.Where(static entry => entry.IsPackageBacked))
        {
            if (string.Equals(entry.AssetType, "native", StringComparison.OrdinalIgnoreCase))
            {
                nativeLibraries.Add(new IntegrationPackageNativeLibrary
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

            managedAssemblies.Add(new IntegrationPackageManagedAssembly
            {
                Name = Path.GetFileNameWithoutExtension(entry.RelativePath),
                Culture = TryGetSatelliteCulture(entry),
                Path = entry.SourcePath
            });
        }

        return IntegrationPackageProbeManifest.Create(managedAssemblies, nativeLibraries);
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

    private static string? ComputeProjectLayoutFingerprint(IReadOnlyList<AppHostServerClosureManifestEntry> entries)
    {
        var projectEntries = entries
            .Where(static entry => !entry.IsPackageBacked)
            .Select(GetProjectEntryFingerprint)
            .ToList();

        return projectEntries.Count == 0 ? null : ComputeHash(projectEntries);
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

        return GetProjectEntryFingerprint(entry);
    }

    private static string GetProjectEntryFingerprint(AppHostServerClosureManifestEntry entry)
    {
        if (entry.FileContentHash is { Length: > 0 } fileContentHash)
        {
            return $"libs/{entry.RelativePath}|file|{fileContentHash}";
        }

        throw new InvalidOperationException($"Manifest entry '{entry.RelativePath}' does not contain enough information to compute a fingerprint.");
    }

    private static string ComputeTextHash(string value)
    {
        var hash = new XxHash3();
        hash.Append(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(hash.GetCurrentHash()).ToLowerInvariant();
    }

    internal static string ComputeFileHash(string path, CancellationToken cancellationToken)
    {
        var hash = new XxHash3();
        using var stream = File.OpenRead(path);
        var buffer = new byte[81920];
        int bytesRead;
        while ((bytesRead = stream.Read(buffer)) > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            hash.Append(buffer.AsSpan(0, bytesRead));
        }

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
/// Stores immutable project-reference layouts for the prebuilt AppHost server.
/// </summary>
internal sealed class AppHostServerProjectLayoutStore
{
    private const string LayoutItemsFolderName = "items";
    private const string LayoutManifestFileName = "manifest.txt";
    private const string LayoutRootFolderName = "project-layouts";
    private const string LayoutStagingFolderName = ".staging";

    private static readonly TimeSpan s_stagingCleanupAge = TimeSpan.FromHours(1);

    private readonly string _itemsDirectory;
    private readonly ILogger _logger;
    private readonly string _stagingDirectory;

    public AppHostServerProjectLayoutStore(string rootPath, ILogger logger)
    {
        ArgumentException.ThrowIfNullOrEmpty(rootPath);

        var layoutsDirectory = Path.Combine(Path.GetFullPath(rootPath), LayoutRootFolderName);
        _itemsDirectory = Path.Combine(layoutsDirectory, LayoutItemsFolderName);
        _stagingDirectory = Path.Combine(layoutsDirectory, LayoutStagingFolderName);
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
                _logger.LogDebug(ex, "Failed to clean stale AppHost project layout staging directory {Path}", stagingDirectory);
            }
        }
    }

    public AppHostServerProjectLayout? TryLoadLayout(string? fingerprint, AppHostServerClosureManifest? expectedManifest = null)
    {
        if (string.IsNullOrWhiteSpace(fingerprint))
        {
            return null;
        }

        var layoutPath = GetLayoutPath(fingerprint);
        var libsPath = Path.Combine(layoutPath, "libs");
        var manifestPath = Path.Combine(layoutPath, LayoutManifestFileName);

        if (!Directory.Exists(layoutPath) ||
            !Directory.Exists(libsPath) ||
            !File.Exists(manifestPath))
        {
            return null;
        }

        if (expectedManifest is not null &&
            !IsLayoutCurrent(layoutPath, libsPath, manifestPath, expectedManifest))
        {
            return null;
        }

        return new AppHostServerProjectLayout
        {
            LayoutPath = layoutPath,
            Fingerprint = fingerprint
        };
    }

    public async Task<AppHostServerProjectLayout?> GetOrCreateAsync(
        AppHostServerClosureManifest manifest,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        if (manifest.ProjectLayoutFingerprint is null)
        {
            return null;
        }

        if (TryLoadLayout(manifest.ProjectLayoutFingerprint, manifest) is { } existingLayout)
        {
            return existingLayout;
        }

        DeleteInvalidLayoutDirectory(manifest.ProjectLayoutFingerprint);

        var stagingPath = Path.Combine(_stagingDirectory, Guid.NewGuid().ToString("n"));
        var libsPath = Path.Combine(stagingPath, "libs");

        Directory.CreateDirectory(libsPath);

        try
        {
            foreach (var entry in manifest.Entries.Where(static entry => !entry.IsPackageBacked))
            {
                cancellationToken.ThrowIfCancellationRequested();

                var destinationPath = Path.Combine(libsPath, entry.RelativePath.Replace('/', Path.DirectorySeparatorChar));
                var destinationDirectory = Path.GetDirectoryName(destinationPath);
                if (!string.IsNullOrEmpty(destinationDirectory))
                {
                    Directory.CreateDirectory(destinationDirectory);
                }

                File.Copy(entry.SourcePath, destinationPath, overwrite: false);
            }

            await WriteManifestFileAsync(Path.Combine(stagingPath, LayoutManifestFileName), manifest, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            TryDeleteDirectory(stagingPath);
            throw;
        }

        var finalLayoutPath = GetLayoutPath(manifest.ProjectLayoutFingerprint);
        try
        {
            Directory.Move(stagingPath, finalLayoutPath);

            _logger.LogInformation("Created AppHost project layout {LayoutFingerprint}", manifest.ProjectLayoutFingerprint);

            return new AppHostServerProjectLayout
            {
                LayoutPath = finalLayoutPath,
                Fingerprint = manifest.ProjectLayoutFingerprint
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            TryDeleteDirectory(stagingPath);

            if (TryLoadLayout(manifest.ProjectLayoutFingerprint, manifest) is { } recoveredLayout)
            {
                return recoveredLayout;
            }

            throw;
        }
    }

    private string GetLayoutPath(string fingerprint)
    {
        return Path.Combine(_itemsDirectory, fingerprint);
    }

    private static bool IsLayoutCurrent(
        string layoutPath,
        string libsPath,
        string manifestPath,
        AppHostServerClosureManifest expectedManifest)
    {
        try
        {
            if (!File.ReadLines(manifestPath).SequenceEqual(expectedManifest.GetProjectLayoutManifestLines(), StringComparer.Ordinal))
            {
                return false;
            }

            foreach (var entry in expectedManifest.Entries.Where(static entry => !entry.IsPackageBacked))
            {
                var copiedPath = Path.Combine(libsPath, entry.RelativePath.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(copiedPath) ||
                    !string.Equals(AppHostServerClosureManifest.ComputeFileHash(copiedPath, CancellationToken.None), entry.FileContentHash, StringComparison.Ordinal))
                {
                    return false;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }

        return Directory.Exists(layoutPath);
    }

    private void DeleteInvalidLayoutDirectory(string fingerprint)
    {
        var layoutPath = GetLayoutPath(fingerprint);
        if (!Directory.Exists(layoutPath))
        {
            return;
        }

        _logger.LogInformation("Discarding invalid AppHost project layout {LayoutFingerprint}", fingerprint);
        TryDeleteDirectory(layoutPath);
    }

    private static async Task WriteManifestFileAsync(
        string path,
        AppHostServerClosureManifest manifest,
        CancellationToken cancellationToken)
    {
        await File.WriteAllLinesAsync(path, manifest.GetProjectLayoutManifestLines(), cancellationToken).ConfigureAwait(false);
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
            _logger.LogDebug(ex, "Failed to delete AppHost project layout directory {Path}", path);
        }
    }
}
