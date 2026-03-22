// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using Aspire.Cli.Layout;
using Aspire.Cli.Utils;
using Aspire.Shared;
using Microsoft.Extensions.Logging;

namespace Aspire.Cli.Bundles;

/// <summary>
/// Manages extraction of the embedded bundle payload from self-extracting CLI binaries.
/// </summary>
internal sealed class BundleService(ILayoutDiscovery layoutDiscovery, ILogger<BundleService> logger) : IBundleService
{
    private const string PayloadResourceName = "bundle.tar.gz";

    /// <summary>
    /// Name of the marker file written after successful extraction.
    /// </summary>
    internal const string VersionMarkerFileName = ".aspire-bundle-version";

    private static readonly bool s_isBundle =
        typeof(BundleService).Assembly.GetManifestResourceInfo(PayloadResourceName) is not null;

    /// <inheritdoc/>
    public bool IsBundle => s_isBundle;

    /// <summary>
    /// Opens a read-only stream over the embedded bundle payload.
    /// Returns <see langword="null"/> if no payload is embedded.
    /// </summary>
    public static Stream? OpenPayload() =>
        typeof(BundleService).Assembly.GetManifestResourceStream(PayloadResourceName);

    /// <summary>
    /// Well-known layout subdirectories that are cleaned before re-extraction.
    /// The bin/ directory is intentionally excluded since it contains the running CLI binary.
    /// </summary>
    internal static readonly string[] s_layoutDirectories = [
        BundleDiscovery.ManagedDirectoryName,
        BundleDiscovery.DcpDirectoryName
    ];

    /// <inheritdoc/>
    public async Task EnsureExtractedAsync(CancellationToken cancellationToken = default)
    {
        if (!IsBundle)
        {
            logger.LogDebug("No embedded bundle payload, skipping extraction.");
            return;
        }

        var processPath = Environment.ProcessPath;
        if (string.IsNullOrEmpty(processPath))
        {
            logger.LogDebug("ProcessPath is null or empty, skipping bundle extraction.");
            return;
        }

        var extractDir = GetDefaultExtractDir(processPath);
        if (extractDir is null)
        {
            logger.LogDebug("Could not determine extraction directory from {ProcessPath}, skipping.", processPath);
            return;
        }

        logger.LogDebug("Ensuring bundle is extracted to {ExtractDir}.", extractDir);
        var result = await ExtractAsync(extractDir, force: false, cancellationToken);

        if (result is BundleExtractResult.ExtractionFailed)
        {
            throw new InvalidOperationException(
                "Bundle extraction failed. Run 'aspire setup --force' to retry, or reinstall the Aspire CLI.");
        }
    }

    /// <inheritdoc/>
    public async Task<LayoutConfiguration?> EnsureExtractedAndGetLayoutAsync(CancellationToken cancellationToken = default)
    {
        await EnsureExtractedAsync(cancellationToken).ConfigureAwait(false);
        return layoutDiscovery.DiscoverLayout();
    }

    /// <inheritdoc/>
    public async Task<BundleExtractResult> ExtractAsync(string destinationPath, bool force = false, CancellationToken cancellationToken = default)
    {
        if (!IsBundle)
        {
            logger.LogDebug("No embedded bundle payload.");
            return BundleExtractResult.NoPayload;
        }

        // Use a file lock for cross-process synchronization
        var lockPath = Path.Combine(destinationPath, ".aspire-bundle-lock");
        logger.LogDebug("Acquiring bundle extraction lock at {LockPath}...", lockPath);
        using var fileLock = await FileLock.AcquireAsync(lockPath, cancellationToken).ConfigureAwait(false);
        logger.LogDebug("Bundle extraction lock acquired.");

        try
        {
            // Re-check after acquiring lock — another process may have already extracted
            if (!force && layoutDiscovery.DiscoverLayout() is not null)
            {
                var existingVersion = ReadVersionMarker(destinationPath);
                var currentVersion = GetCurrentVersion();
                if (existingVersion == currentVersion)
                {
                    logger.LogDebug("Bundle already extracted and up to date (version: {Version}).", existingVersion);
                    return BundleExtractResult.AlreadyUpToDate;
                }

                logger.LogDebug("Version mismatch: existing={ExistingVersion}, current={CurrentVersion}. Re-extracting.", existingVersion, currentVersion);
            }

            return await ExtractCoreAsync(destinationPath, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to extract bundle to {Path}", destinationPath);
            return BundleExtractResult.ExtractionFailed;
        }
    }

    private async Task<BundleExtractResult> ExtractCoreAsync(string destinationPath, CancellationToken cancellationToken)
    {
        logger.LogInformation("Extracting embedded bundle to {Path}...", destinationPath);

        // Clean existing layout directories before extraction to avoid file conflicts
        logger.LogDebug("Cleaning existing layout directories in {Path}.", destinationPath);
        CleanLayoutDirectories(destinationPath);

        var sw = Stopwatch.StartNew();
        await ExtractPayloadAsync(destinationPath, cancellationToken);
        sw.Stop();
        logger.LogDebug("Payload extraction completed in {ElapsedMs}ms.", sw.ElapsedMilliseconds);

        // Write version marker so subsequent runs skip extraction
        var currentVersion = GetCurrentVersion();
        WriteVersionMarker(destinationPath, currentVersion);
        logger.LogDebug("Version marker written (version: {Version}).", currentVersion);

        // Verify extraction produced a valid layout
        if (layoutDiscovery.DiscoverLayout() is null)
        {
            logger.LogError("Extraction completed but no valid layout found in {Path}.", destinationPath);
            return BundleExtractResult.ExtractionFailed;
        }

        logger.LogDebug("Bundle extraction verified successfully.");
        return BundleExtractResult.Extracted;
    }

    /// <summary>
    /// Determines the default extraction directory for the current CLI binary.
    /// If CLI is at ~/.aspire/bin/aspire, returns ~/.aspire/ so layout discovery
    /// finds components via the bin/ layout pattern.
    /// </summary>
    internal static string? GetDefaultExtractDir(string processPath)
    {
        var cliDir = Path.GetDirectoryName(processPath);
        if (string.IsNullOrEmpty(cliDir))
        {
            return null;
        }

        return Path.GetDirectoryName(cliDir) ?? cliDir;
    }

    /// <summary>
    /// Removes well-known layout subdirectories before re-extraction.
    /// Preserves the bin/ directory (which contains the CLI binary itself).
    /// </summary>
    internal static void CleanLayoutDirectories(string layoutPath)
    {
        foreach (var dir in s_layoutDirectories)
        {
            var fullPath = Path.Combine(layoutPath, dir);
            if (Directory.Exists(fullPath))
            {
                Directory.Delete(fullPath, recursive: true);
            }
        }

        // Remove version marker so it's rewritten after extraction
        var markerPath = Path.Combine(layoutPath, VersionMarkerFileName);
        if (File.Exists(markerPath))
        {
            File.Delete(markerPath);
        }
    }

    /// <summary>
    /// Gets the assembly informational version of the current CLI binary.
    /// Used as the version marker to detect when re-extraction is needed.
    /// </summary>
    internal static string GetCurrentVersion()
    {
        return VersionHelper.GetDefaultTemplateVersion();
    }

    /// <summary>
    /// Writes a version marker file to the extraction directory.
    /// </summary>
    internal static void WriteVersionMarker(string extractDir, string version)
    {
        var markerPath = Path.Combine(extractDir, VersionMarkerFileName);
        File.WriteAllText(markerPath, version);
    }

    /// <summary>
    /// Reads the version string from a previously written marker file.
    /// Returns null if the marker doesn't exist or is empty.
    /// </summary>
    internal static string? ReadVersionMarker(string extractDir)
    {
        var markerPath = Path.Combine(extractDir, VersionMarkerFileName);
        if (!File.Exists(markerPath))
        {
            return null;
        }

        var content = File.ReadAllText(markerPath).Trim();
        return string.IsNullOrEmpty(content) ? null : content;
    }

    /// <summary>
    /// Extracts the embedded tar.gz payload to the specified directory using .NET TarReader.
    /// </summary>
    internal static async Task ExtractPayloadAsync(string destinationPath, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(destinationPath);

        using var payloadStream = OpenPayload() ?? throw new InvalidOperationException("No embedded bundle payload.");
        await using var gzipStream = new GZipStream(payloadStream, CompressionMode.Decompress);
        await using var tarReader = new TarReader(gzipStream);

        while (await tarReader.GetNextEntryAsync(cancellationToken: cancellationToken) is { } entry)
        {
            // Strip the top-level directory (equivalent to tar --strip-components=1)
            var name = entry.Name;
            var slashIndex = name.IndexOf('/');
            if (slashIndex < 0)
            {
                continue; // Top-level directory entry itself, skip
            }

            var relativePath = name[(slashIndex + 1)..];
            if (string.IsNullOrEmpty(relativePath))
            {
                continue;
            }

            var fullPath = Path.GetFullPath(Path.Combine(destinationPath, relativePath));
            var normalizedDestination = Path.GetFullPath(destinationPath);

            // Guard against path traversal attacks (e.g., entries containing ".." segments)
            if (!fullPath.StartsWith(normalizedDestination + Path.DirectorySeparatorChar, StringComparison.Ordinal) &&
                !fullPath.Equals(normalizedDestination, StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Tar entry '{entry.Name}' would extract outside the destination directory.");
            }

            switch (entry.EntryType)
            {
                case TarEntryType.Directory:
                    Directory.CreateDirectory(fullPath);
                    break;

                case TarEntryType.RegularFile:
                    var dir = Path.GetDirectoryName(fullPath);
                    if (dir is not null)
                    {
                        Directory.CreateDirectory(dir);
                    }
                    await entry.ExtractToFileAsync(fullPath, overwrite: true, cancellationToken);

                    // Preserve Unix file permissions from tar entry (e.g., execute bit)
                    if (!OperatingSystem.IsWindows() && entry.Mode != default)
                    {
                        File.SetUnixFileMode(fullPath, (UnixFileMode)entry.Mode);
                    }
                    break;

                case TarEntryType.SymbolicLink:
                    if (string.IsNullOrEmpty(entry.LinkName))
                    {
                        continue;
                    }
                    // Validate symlink target stays within the extraction directory
                    var linkTarget = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(fullPath)!, entry.LinkName));
                    if (!linkTarget.StartsWith(normalizedDestination + Path.DirectorySeparatorChar, StringComparison.Ordinal) &&
                        !linkTarget.Equals(normalizedDestination, StringComparison.Ordinal))
                    {
                        throw new InvalidOperationException($"Symlink '{entry.Name}' targets '{entry.LinkName}' which resolves outside the destination directory.");
                    }
                    var linkDir = Path.GetDirectoryName(fullPath);
                    if (linkDir is not null)
                    {
                        Directory.CreateDirectory(linkDir);
                    }
                    if (File.Exists(fullPath))
                    {
                        File.Delete(fullPath);
                    }
                    File.CreateSymbolicLink(fullPath, entry.LinkName);
                    break;
            }
        }
    }
}
