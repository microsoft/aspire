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
    private const string AssemblyLoadFailureIndicator = "Could not load file or assembly";

    /// <summary>
    /// Name of the marker file written after successful extraction.
    /// </summary>
    internal const string VersionMarkerFileName = ".aspire-bundle-version";

    /// <summary>
    /// Name of the marker file written while extraction is in progress.
    /// </summary>
    internal const string ExtractionInProgressMarkerFileName = ".aspire-bundle-extracting";

    private static readonly bool s_isBundle =
        typeof(BundleService).Assembly.GetManifestResourceInfo(PayloadResourceName) is not null;

    private static readonly string[] s_bundleCorruptionIndicators =
    [
        "System.Private.CoreLib.dll",
        "Failed to create CoreCLR",
        "aspire-managed not found in layout"
    ];

    private static readonly string[] s_managedLayoutIndicators =
    [
        "aspire-managed",
        $"{Path.DirectorySeparatorChar}{BundleDiscovery.ManagedDirectoryName}{Path.DirectorySeparatorChar}",
        "/managed/",
        "\\managed\\"
    ];

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

        var extractDir = TryGetDefaultExtractDir();
        if (extractDir is null)
        {
            logger.LogDebug("Could not determine extraction directory from {ProcessPath}, skipping.", Environment.ProcessPath);
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
    public BundleLayoutState GetLayoutState(string? destinationPath = null)
        => BundleLayoutState.Inspect(destinationPath ?? TryGetDefaultExtractDir());

    /// <inheritdoc/>
    public Task<BundleExtractResult> RepairAsync(string? destinationPath = null, CancellationToken cancellationToken = default)
    {
        if (!IsBundle)
        {
            logger.LogDebug("No embedded bundle payload, skipping repair.");
            return Task.FromResult(BundleExtractResult.NoPayload);
        }

        destinationPath ??= TryGetDefaultExtractDir();
        if (destinationPath is null)
        {
            logger.LogDebug("Could not determine extraction directory from {ProcessPath}, skipping repair.", Environment.ProcessPath);
            return Task.FromResult(BundleExtractResult.ExtractionFailed);
        }

        return ExtractAsync(destinationPath, force: true, cancellationToken);
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
            // This marker is only written by newer CLIs. If it survives, a previous
            // extraction cleaned the layout but never published a complete one.
            if (HasExtractionInProgressMarker(destinationPath))
            {
                logger.LogWarning("Found bundle extraction in-progress marker at {Path}. Re-extracting to recover from an incomplete prior extraction.",
                    destinationPath);
            }

            // Re-check after acquiring lock — another process may have already extracted.
            // "Usable" intentionally includes legacy markerless layouts so new code can
            // continue to run against old installs. We still require a matching version
            // marker before we skip extraction; markerless legacy layouts fall through
            // to re-extract because we cannot prove they match this CLI binary.
            if (!force &&
                IsUsableExtractedLayout(destinationPath))
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
        var currentVersion = GetCurrentVersion();

        // Clean existing layout directories before extraction to avoid file conflicts
        logger.LogDebug("Cleaning existing layout directories in {Path}.", destinationPath);
        CleanLayoutDirectories(destinationPath);

        // Publish the in-progress marker before unpacking files so any concurrent or
        // future discovery can distinguish "rewrite interrupted" from "legacy layout
        // that predates markers entirely".
        WriteExtractionInProgressMarker(destinationPath, currentVersion);
        logger.LogDebug("Extraction in-progress marker written (version: {Version}).", currentVersion);

        var sw = Stopwatch.StartNew();
        await ExtractPayloadAsync(destinationPath, cancellationToken);
        sw.Stop();
        logger.LogDebug("Payload extraction completed in {ElapsedMs}ms.", sw.ElapsedMilliseconds);

        // Verify extraction produced the required on-disk layout before declaring success.
        if (!LayoutDiscovery.HasRequiredLayoutStructure(destinationPath))
        {
            logger.LogError("Extraction completed but the required bundle layout structure was not found in {Path}.", destinationPath);
            return BundleExtractResult.ExtractionFailed;
        }

        // Write version marker so subsequent runs can recognize a complete layout.
        WriteVersionMarker(destinationPath, currentVersion);
        logger.LogDebug("Version marker written (version: {Version}).", currentVersion);

        // Remove the in-progress marker only after the success marker is present so
        // every observable state is unambiguous:
        // - extracting marker present => incomplete
        // - version marker present, extracting marker absent => complete
        DeleteExtractionInProgressMarker(destinationPath);

        // Final validation checks the explicit extraction target rather than rediscovering through ambient state.
        if (!LayoutDiscovery.HasRequiredLayoutStructure(destinationPath) || !IsExtractionComplete(destinationPath))
        {
            logger.LogError("Extraction completed but the marker state or required layout structure was invalid in {Path}. Restoring incomplete-extraction marker.",
                destinationPath);
            WriteExtractionInProgressMarker(destinationPath, currentVersion);
            DeleteVersionMarker(destinationPath);
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
    /// If a directory cannot be deleted (e.g. a process is still using files
    /// inside it), it is renamed to {dir}.old.{timestamp} so extraction can
    /// proceed. The stale directory will be cleaned up on the next successful
    /// extraction.
    /// </summary>
    internal static void CleanLayoutDirectories(string layoutPath)
    {
        foreach (var dir in s_layoutDirectories)
        {
            // Clean up stale .old directories from previous runs first
            FileDeleteHelper.TryCleanupOldItems(layoutPath, dir);

            var fullPath = Path.Combine(layoutPath, dir);
            FileDeleteHelper.TryDeleteDirectory(fullPath);
        }

        DeleteVersionMarker(layoutPath);
        DeleteExtractionInProgressMarker(layoutPath);
    }

    /// <summary>
    /// Gets a fingerprint for the current CLI bundle.
    /// Used as the version marker to detect when re-extraction is needed.
    /// </summary>
    internal static string GetCurrentVersion(string? processPath = null)
    {
        var version = VersionHelper.GetDefaultTemplateVersion();
        processPath ??= Environment.ProcessPath;

        if (string.IsNullOrEmpty(processPath))
        {
            return version;
        }

        try
        {
            var fileInfo = new FileInfo(processPath);
            if (!fileInfo.Exists)
            {
                return version;
            }

            return $"{version}|{fileInfo.Length}|{fileInfo.LastWriteTimeUtc.Ticks}";
        }
        catch (IOException)
        {
            return version;
        }
        catch (UnauthorizedAccessException)
        {
            return version;
        }
        catch (NotSupportedException)
        {
            return version;
        }
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
    /// Writes an extraction in-progress marker file to the extraction directory.
    /// </summary>
    internal static void WriteExtractionInProgressMarker(string extractDir, string version)
    {
        Directory.CreateDirectory(extractDir);
        var markerPath = Path.Combine(extractDir, ExtractionInProgressMarkerFileName);
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
    /// Returns whether an extraction in-progress marker exists.
    /// </summary>
    internal static bool HasExtractionInProgressMarker(string extractDir)
        => BundleLayoutState.Inspect(extractDir).HasExtractionInProgressMarker;

    /// <summary>
    /// Returns whether the extracted layout is marked complete under the
    /// current marker protocol.
    /// </summary>
    internal static bool IsExtractionComplete(string extractDir)
        => BundleLayoutState.Inspect(extractDir).IsExtractionComplete;

    /// <summary>
    /// Returns whether the extracted layout is usable, including legacy layouts
    /// that predate extraction markers but still have the required structure.
    /// This is broader than <see cref="IsExtractionComplete"/> and must not be
    /// treated as evidence that the layout matches the current CLI version.
    /// </summary>
    internal static bool IsUsableExtractedLayout(string extractDir)
        => BundleLayoutState.Inspect(extractDir).IsUsableExtractedLayout;

    /// <summary>
    /// Returns whether failure evidence from the extracted <c>aspire-managed</c> process
    /// looks like bundle corruption rather than an unrelated command failure.
    /// </summary>
    internal static bool LooksLikeBundleCorruption(
        Exception? exception = null,
        string? error = null,
        string? output = null,
        IEnumerable<string>? additionalLines = null,
        string? bundleRoot = null)
    {
        if (BundleLayoutState.Inspect(bundleRoot).IsIncompleteLayout)
        {
            return true;
        }

        var evidence = string.Join(Environment.NewLine, EnumerateFailureEvidence(exception, error, output, additionalLines));

        return ContainsAny(evidence, s_bundleCorruptionIndicators) || HasManagedLayoutAssemblyLoadFailure(evidence);
    }

    private static string? TryGetDefaultExtractDir()
    {
        var processPath = Environment.ProcessPath;
        if (string.IsNullOrEmpty(processPath))
        {
            return null;
        }

        return GetDefaultExtractDir(processPath);
    }

    private static void DeleteVersionMarker(string extractDir)
    {
        var markerPath = Path.Combine(extractDir, VersionMarkerFileName);
        if (File.Exists(markerPath))
        {
            File.Delete(markerPath);
        }
    }

    private static void DeleteExtractionInProgressMarker(string extractDir)
    {
        var markerPath = Path.Combine(extractDir, ExtractionInProgressMarkerFileName);
        if (File.Exists(markerPath))
        {
            File.Delete(markerPath);
        }
    }

    private static IEnumerable<string> EnumerateFailureEvidence(
        Exception? exception,
        string? error,
        string? output,
        IEnumerable<string>? additionalLines)
    {
        if (exception is not null)
        {
            yield return exception.ToString();
        }

        if (!string.IsNullOrWhiteSpace(error))
        {
            yield return error;
        }

        if (!string.IsNullOrWhiteSpace(output))
        {
            yield return output;
        }

        if (additionalLines is null)
        {
            yield break;
        }

        foreach (var line in additionalLines)
        {
            if (!string.IsNullOrWhiteSpace(line))
            {
                yield return line;
            }
        }
    }

    private static bool HasManagedLayoutAssemblyLoadFailure(string evidence)
    {
        return evidence.Contains(AssemblyLoadFailureIndicator, StringComparison.OrdinalIgnoreCase)
            && ContainsAny(evidence, s_managedLayoutIndicators);
    }

    private static bool ContainsAny(string evidence, IEnumerable<string> indicators)
    {
        foreach (var indicator in indicators)
        {
            if (evidence.Contains(indicator, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
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
