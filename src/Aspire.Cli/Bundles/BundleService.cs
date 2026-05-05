// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.IO.Hashing;
using System.Text;
using Aspire.Cli.Layout;
using Aspire.Cli.Utils;
using Aspire.Shared;
using Microsoft.Extensions.Logging;

namespace Aspire.Cli.Bundles;

/// <summary>
/// Manages extraction of the embedded bundle payload from self-extracting CLI binaries.
/// </summary>
internal sealed class BundleService(IBundlePayloadProvider payloadProvider, ILayoutDiscovery layoutDiscovery, ILogger<BundleService> logger) : IBundleService
{
    /// <summary>
    /// Name of the marker file written after successful extraction.
    /// </summary>
    internal const string VersionMarkerFileName = ".aspire-bundle-version";

    /// <summary>
    /// Directory under the layout root containing per-version bundle installations.
    /// </summary>
    internal const string VersionsDirectoryName = "versions";

    /// <summary>
    /// Suffix appended to an in-progress extraction directory so it is ignored by
    /// layout discovery and can be atomically renamed to its final name only after
    /// extraction completes.
    /// </summary>
    internal const string TempSuffixPrefix = ".tmp.";

    /// <summary>
    /// Suffix appended to a versioned directory that failed verification. Retained
    /// on disk (with the version-id fingerprint) so the fingerprint-match
    /// short-circuit cannot accidentally promote a known-bad payload on a later run.
    /// </summary>
    internal const string BadSuffixPrefix = ".bad.";

    /// <inheritdoc/>
    public bool IsBundle => payloadProvider.HasPayload;

    /// <summary>
    /// Overrides <see cref="Environment.ProcessPath"/> for version fingerprinting.
    /// Used in tests to simulate different CLI binaries.
    /// </summary>
    internal string? ProcessPathOverride { get; init; }

    /// <summary>
    /// Well-known layout subdirectory that is exposed as a reparse point pointing
    /// at the active versioned bundle directory. Components (<c>managed/</c> and
    /// <c>dcp/</c>) are resolved as subdirectories of this link target.
    /// </summary>
    internal static readonly string[] s_linkedLayoutDirectories = [
        BundleDiscovery.BundleDirectoryName,
    ];

    /// <inheritdoc/>
    public async Task EnsureExtractedAsync(CancellationToken cancellationToken = default)
    {
        if (!IsBundle)
        {
            logger.LogDebug("No embedded bundle payload, skipping extraction.");
            return;
        }

        var processPath = ProcessPathOverride ?? Environment.ProcessPath;
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
                var currentVersion = GetCurrentVersion(ProcessPathOverride);
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

        Directory.CreateDirectory(destinationPath);
        var versionsRoot = Path.Combine(destinationPath, VersionsDirectoryName);
        Directory.CreateDirectory(versionsRoot);

        var currentVersion = GetCurrentVersion(ProcessPathOverride);
        var versionId = ComputeVersionId(currentVersion);
        var activeVersionDir = Path.Combine(versionsRoot, versionId);

        // Reuse an already-extracted versioned directory if it passes validation.
        // This handles the case where the marker / links were deleted but the
        // payload is still intact on disk.
        if (!IsVersionedLayoutValid(activeVersionDir))
        {
            logger.LogDebug("Versioned layout {Path} not valid or missing; extracting fresh.", activeVersionDir);
            if (!await ExtractVersionedLayoutAsync(versionsRoot, versionId, activeVersionDir, cancellationToken).ConfigureAwait(false))
            {
                return BundleExtractResult.ExtractionFailed;
            }
        }
        else
        {
            logger.LogDebug("Reusing existing versioned layout at {Path}.", activeVersionDir);
        }

        // Capture prior link targets before flipping so we can roll back if the
        // post-flip sanity check fails.
        var priorTargets = CaptureLinkTargets(destinationPath);

        // Migrate any legacy real directories (managed/, dcp/) and flip the public
        // reparse points to point at the new versioned directory.
        if (!TryFlipLinks(destinationPath, activeVersionDir))
        {
            logger.LogError("Failed to flip bundle links to {VersionDir}.", activeVersionDir);
            return BundleExtractResult.ExtractionFailed;
        }

        // Post-flip sanity check: confirm layout discovery resolves through the
        // new reparse points. Roll back to the prior targets on failure.
        if (layoutDiscovery.DiscoverLayout() is null)
        {
            logger.LogError("Post-flip layout validation failed; attempting rollback.");
            if (!TryRestoreLinks(destinationPath, priorTargets))
            {
                logger.LogError("Rollback of bundle links failed; layout is in an inconsistent state.");
            }
            return BundleExtractResult.ExtractionFailed;
        }

        // Write version marker so subsequent runs can short-circuit.
        WriteVersionMarker(destinationPath, currentVersion);
        logger.LogDebug("Version marker written (version: {Version}).", currentVersion);

        // Best-effort cleanup of non-active versioned directories and any stale
        // .tmp.*, .bad.*, .old.* siblings.
        TryCleanupStaleVersions(versionsRoot, versionId);

        // Best-effort cleanup of .old legacy directories created during this
        // migration. These are safe to remove now that post-flip validation passed.
        foreach (var dir in s_linkedLayoutDirectories)
        {
            FileDeleteHelper.TryCleanupOldItems(destinationPath, dir);
        }

        // Best-effort cleanup of legacy top-level managed/ and dcp/ paths from
        // the old layout (before the single bundle/ link was introduced). These
        // are no longer needed now that layout discovery resolves through bundle/.
        TryCleanupLegacyLayoutPaths(destinationPath);

        logger.LogDebug("Bundle extraction verified successfully.");
        return BundleExtractResult.Extracted;
    }

    /// <summary>
    /// Extracts the payload into a <c>.tmp.*</c> sibling of the target versioned
    /// directory, validates the result, and atomically renames it to
    /// <paramref name="activeVersionDir"/>. Returns <see langword="false"/> if
    /// verification fails (in which case the failed directory has been renamed
    /// to <c>.bad.&lt;tick&gt;</c> and logged).
    /// </summary>
    private async Task<bool> ExtractVersionedLayoutAsync(
        string versionsRoot,
        string versionId,
        string activeVersionDir,
        CancellationToken cancellationToken)
    {
        var tempDir = Path.Combine(versionsRoot, $"{versionId}{TempSuffixPrefix}{Guid.NewGuid():N}");

        // Clean up if a previous attempt left a dir with this exact name.
        FileDeleteHelper.TryDeleteDirectory(tempDir);

        var sw = Stopwatch.StartNew();
        try
        {
            await ExtractPayloadAsync(tempDir, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            FileDeleteHelper.TryDeleteDirectory(tempDir);
            throw;
        }
        sw.Stop();
        logger.LogDebug("Payload extraction into {Path} completed in {ElapsedMs}ms.", tempDir, sw.ElapsedMilliseconds);

        // Pre-flip verification: validate the freshly-unpacked bundle before it
        // can become the active version.
        if (!IsVersionedLayoutValid(tempDir))
        {
            var badPath = $"{activeVersionDir}{BadSuffixPrefix}{Environment.TickCount64}";
            logger.LogError("Extracted bundle at {Path} failed verification; renaming to {BadPath}.", tempDir, badPath);
            try
            {
                Directory.Move(tempDir, badPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(ex, "Unable to preserve failed bundle at {BadPath}; deleting instead.", badPath);
                FileDeleteHelper.TryDeleteDirectory(tempDir);
            }
            return false;
        }

        // If a stale activeVersionDir exists (partial prior install), move it aside.
        if (Directory.Exists(activeVersionDir))
        {
            FileDeleteHelper.TryDeleteDirectory(activeVersionDir);
        }

        try
        {
            Directory.Move(tempDir, activeVersionDir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogError(ex, "Failed to promote {TempDir} to {ActiveDir}.", tempDir, activeVersionDir);
            FileDeleteHelper.TryDeleteDirectory(tempDir);
            return false;
        }

        // Re-validate after rename.
        if (!IsVersionedLayoutValid(activeVersionDir))
        {
            var badPath = $"{activeVersionDir}{BadSuffixPrefix}{Environment.TickCount64}";
            logger.LogError("Post-rename validation failed for {Path}; renaming to {BadPath}.", activeVersionDir, badPath);
            try
            {
                Directory.Move(activeVersionDir, badPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(ex, "Unable to preserve failed bundle at {BadPath}.", badPath);
            }
            return false;
        }

        return true;
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
    /// Captures the current reparse-point targets for the public link paths so
    /// they can be restored if the post-flip sanity check fails.
    /// A non-reparse-point path (or missing path) is captured as <see langword="null"/>
    /// meaning "no link to restore".
    /// </summary>
    internal static IReadOnlyDictionary<string, string?> CaptureLinkTargets(string layoutPath)
    {
        var targets = new Dictionary<string, string?>(s_linkedLayoutDirectories.Length, StringComparer.Ordinal);
        foreach (var dir in s_linkedLayoutDirectories)
        {
            var linkPath = Path.Combine(layoutPath, dir);
            targets[dir] = ReparsePoint.IsReparsePoint(linkPath) ? ReparsePoint.GetTarget(linkPath) : null;
        }
        return targets;
    }

    /// <summary>
    /// Points the public <c>bundle/</c> link at the active versioned directory.
    /// Migrates any legacy real directory sitting at the link path by renaming it
    /// to a <c>.old</c> sibling (preserved until post-flip validation succeeds).
    /// </summary>
    private bool TryFlipLinks(string layoutPath, string activeVersionDir)
    {
        foreach (var dir in s_linkedLayoutDirectories)
        {
            var linkPath = Path.Combine(layoutPath, dir);

            // The bundle link points directly at the active version directory —
            // components (managed/, dcp/) are subdirectories of the target.
            var target = activeVersionDir;

            // Clear out legacy stale siblings from prior runs first.
            FileDeleteHelper.TryCleanupOldItems(layoutPath, dir);

            // If a legacy real directory is sitting at the public path, rename it
            // to a .old sibling so a reparse point can be created. The .old sibling
            // is preserved until after post-flip validation succeeds.
            if (Directory.Exists(linkPath) && !ReparsePoint.IsReparsePoint(linkPath))
            {
                var renamedPath = $"{linkPath}.old.{Environment.TickCount64}";
                logger.LogDebug("Migrating legacy directory at {Path} to {Renamed}.", linkPath, renamedPath);
                try
                {
                    Directory.Move(linkPath, renamedPath);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    logger.LogError(ex, "Failed to rename legacy directory {Path}.", linkPath);
                    return false;
                }
            }

            try
            {
                ReparsePoint.CreateOrReplace(linkPath, target);
                logger.LogDebug("Linked {Link} -> {Target}", linkPath, target);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogError(ex, "Failed to create reparse point at {Path} -> {Target}.", linkPath, target);
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Best-effort restore of link targets captured before a failed flip. Entries
    /// whose prior value was <see langword="null"/> (no previous link) are removed.
    /// </summary>
    private bool TryRestoreLinks(string layoutPath, IReadOnlyDictionary<string, string?> priorTargets)
    {
        var allOk = true;
        foreach (var (dir, priorTarget) in priorTargets)
        {
            var linkPath = Path.Combine(layoutPath, dir);
            try
            {
                if (priorTarget is null)
                {
                    ReparsePoint.RemoveIfExists(linkPath);
                }
                else
                {
                    ReparsePoint.CreateOrReplace(linkPath, priorTarget);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogError(ex, "Rollback failed for link {Path}.", linkPath);
                allOk = false;
            }
        }
        return allOk;
    }

    /// <summary>
    /// Returns <see langword="true"/> if <paramref name="versionDir"/> contains the
    /// essential bundle components (<c>managed/aspire-managed</c> and a DCP directory).
    /// </summary>
    internal static bool IsVersionedLayoutValid(string versionDir)
    {
        if (!Directory.Exists(versionDir))
        {
            return false;
        }

        var managedDir = Path.Combine(versionDir, BundleDiscovery.ManagedDirectoryName);
        var managedExe = Path.Combine(managedDir, BundleDiscovery.GetExecutableFileName(BundleDiscovery.ManagedExecutableName));

        if (!Directory.Exists(managedDir) || !File.Exists(managedExe))
        {
            return false;
        }

        try
        {
            var info = new FileInfo(managedExe);
            if (info.Length == 0)
            {
                return false;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }

        var dcpDir = Path.Combine(versionDir, BundleDiscovery.DcpDirectoryName);
        if (!Directory.Exists(dcpDir))
        {
            return false;
        }

        return true;
    }

    /// <summary>
    /// Best-effort sweep of the <c>versions/</c> directory, removing anything other
    /// than the active version plus any <c>.tmp.*</c>, <c>.bad.*</c>, <c>.old.*</c>
    /// leftovers. Locked items are softly renamed so they can be reaped next run.
    /// </summary>
    internal static void TryCleanupStaleVersions(string versionsRoot, string activeVersionId)
    {
        if (!Directory.Exists(versionsRoot))
        {
            return;
        }

        foreach (var entry in Directory.EnumerateDirectories(versionsRoot))
        {
            var name = Path.GetFileName(entry);

            // Keep the active version.
            if (string.Equals(name, activeVersionId, StringComparison.Ordinal))
            {
                continue;
            }

            try
            {
                Directory.Delete(entry, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Still in use — rename so it is out of the way and will be reaped next run.
                try
                {
                    Directory.Move(entry, $"{entry}.old.{Environment.TickCount64}");
                }
                catch
                {
                }
            }
        }
    }

    /// <summary>
    /// Best-effort removal of legacy top-level <c>managed/</c> and <c>dcp/</c>
    /// directories from the old layout shape (before the single <c>bundle/</c> link
    /// was introduced). Failures are silently ignored since the new layout via
    /// <c>bundle/</c> is already functional.
    /// </summary>
    private void TryCleanupLegacyLayoutPaths(string layoutPath)
    {
        string[] legacyDirs = [BundleDiscovery.ManagedDirectoryName, BundleDiscovery.DcpDirectoryName];

        foreach (var dir in legacyDirs)
        {
            var legacyPath = Path.Combine(layoutPath, dir);
            if (!Directory.Exists(legacyPath))
            {
                continue;
            }

            try
            {
                FileDeleteHelper.TryDeleteDirectory(legacyPath);
                logger.LogDebug("Removed legacy directory at {Path}.", legacyPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogDebug(ex, "Could not remove legacy path {Path}; will retry next run.", legacyPath);
            }
        }
    }

    /// <summary>
    /// Computes a deterministic, filesystem-safe directory name for a given
    /// current-version fingerprint. The fingerprint already captures the CLI
    /// binary's size and timestamp, so the resulting id changes whenever the
    /// payload would change.
    /// </summary>
    /// <remarks>
    /// Format: <c>&lt;sanitized-version&gt;-&lt;64-bit-xxhash-hex&gt;</c>. Version
    /// characters outside <c>[A-Za-z0-9._-]</c> are replaced with <c>_</c>.
    /// </remarks>
    internal static string ComputeVersionId(string currentVersion)
    {
        var hashBytes = XxHash3.Hash(Encoding.UTF8.GetBytes(currentVersion));
        var hash = Convert.ToHexString(hashBytes).ToLowerInvariant();

        // Extract the human-readable prefix (everything before the first '|') for
        // readability in the on-disk layout, and sanitize for filesystem safety.
        var separatorIndex = currentVersion.IndexOf('|');
        var versionPart = separatorIndex >= 0 ? currentVersion[..separatorIndex] : currentVersion;

        var sb = new StringBuilder(versionPart.Length);
        foreach (var ch in versionPart)
        {
            sb.Append(ch is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9') or '.' or '-' or '_'
                ? ch
                : '_');
        }

        var prefix = sb.Length == 0 ? "bundle" : sb.ToString();
        return $"{prefix}-{hash}";
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
    internal async Task ExtractPayloadAsync(string destinationPath, CancellationToken cancellationToken)
    {
        using var payloadStream = payloadProvider.OpenPayload() ?? throw new InvalidOperationException("No bundle payload available.");
        await ExtractPayloadAsync(payloadStream, destinationPath, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Extracts a tar.gz payload stream to the specified directory.
    /// </summary>
    internal static async Task ExtractPayloadAsync(Stream payloadStream, string destinationPath, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(destinationPath);

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
