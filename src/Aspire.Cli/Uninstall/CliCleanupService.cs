// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Acquisition;
using Aspire.Cli.Bundles;
using Aspire.Cli.Configuration;
using Aspire.Cli.Utils;
using Aspire.Shared;

namespace Aspire.Cli.Uninstall;

/// <summary>
/// Removes Aspire CLI install state: package hives, dogfood installs, the
/// shared script install layout under <c>~/.aspire/{bin,bundle,versions}</c>,
/// and matching global-channel settings. Read-only hive enumeration lives on
/// <see cref="HiveEnumerator"/>.
/// </summary>
internal sealed class CliCleanupService(HiveEnumerator hives, CliExecutionContext executionContext, IConfigurationService configurationService)
{
    private static readonly StringComparison s_pathComparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    public IReadOnlyList<string> ExpandChannels(string? channel, bool all)
    {
        if (!all)
        {
            return string.IsNullOrWhiteSpace(channel) ? [] : [channel];
        }

        return hives.GetHives()
            .Select(h => h.Name)
            .Where(IsIncludedInAll)
            .ToList();
    }

    public async Task<CleanupResult> DeleteHiveAsync(string channel, bool force, bool dryRun, CancellationToken cancellationToken)
    {
        ValidateChannel(channel);
        var operations = new List<CleanupOperation>();
        var currentProcessPath = CliPathHelper.ResolveSymlinkToFullPath(Environment.ProcessPath);
        var hiveDirectory = GetHiveDirectory(channel);
        var dogfoodDirectory = GetDogfoodDirectory(channel);

        if (dogfoodDirectory.Exists && !force)
        {
            operations.Add(CleanupOperation.Skipped(
                hiveDirectory.FullName,
                $"A matching dogfood install exists. Run 'aspire uninstall --channel {channel}' to remove both the hive and dogfood install, or pass --force to delete only the hive."));
            return new CleanupResult(operations, HasFailures: true);
        }

        operations.Add(await DeleteDirectoryAsync(hiveDirectory, currentProcessPath, dryRun, cancellationToken));
        return new CleanupResult(operations, operations.Any(o => o.Status is CleanupOperationStatus.Failed));
    }

    public async Task<CleanupResult> UninstallAsync(IReadOnlyList<string> channels, bool removeSharedInstall, bool dryRun, CancellationToken cancellationToken)
    {
        foreach (var channel in channels)
        {
            ValidateChannel(channel);
        }

        var operations = new List<CleanupOperation>();
        var currentProcessPath = CliPathHelper.ResolveSymlinkToFullPath(Environment.ProcessPath);

        foreach (var channel in channels)
        {
            var hiveDirectory = GetHiveDirectory(channel);
            operations.Add(await DeleteDirectoryAsync(hiveDirectory, currentProcessPath, dryRun, cancellationToken));

            if (IsPrChannel(channel))
            {
                var dogfoodDirectory = GetDogfoodDirectory(channel);
                if (dogfoodDirectory.Exists)
                {
                    operations.Add(DeleteDirectoryUnlessRunningFromTarget(dogfoodDirectory, currentProcessPath, dryRun));
                }
            }

            await DeleteMatchingGlobalChannelAsync(channel, dryRun, operations, cancellationToken);
        }

        var sharedRemovalIncomplete = false;
        if (removeSharedInstall)
        {
            sharedRemovalIncomplete = AddSharedInstallOperations(currentProcessPath, dryRun, operations);
        }
        else if (channels.Any(IsSharedScriptChannel) && SharedInstallExists())
        {
            operations.Add(CleanupOperation.Skipped(
                GetSharedBinDirectory().FullName,
                "Shared script install artifacts were left in place. Pass --remove-shared-install to remove ~/.aspire/bin/aspire and the matching bundle/versions layout."));
        }

        // sharedRemovalIncomplete is only honored outside dry-run: dry-run is a
        // describe-only mode and should not fail just because a real cleanup
        // would have been blocked by a lease at runtime.
        var hasFailures = operations.Any(o => o.Status is CleanupOperationStatus.Failed)
            || (sharedRemovalIncomplete && !dryRun);
        return new CleanupResult(operations, hasFailures);
    }

    private async Task DeleteMatchingGlobalChannelAsync(string channel, bool dryRun, List<CleanupOperation> operations, CancellationToken cancellationToken)
    {
        var globalConfig = await configurationService.GetGlobalConfigurationAsync(cancellationToken);
        if (globalConfig.TryGetValue("channel", out var configuredChannel) &&
            string.Equals(configuredChannel, channel, StringComparison.Ordinal))
        {
            if (dryRun)
            {
                operations.Add(CleanupOperation.WouldRemove(configurationService.GetSettingsFilePath(isGlobal: true), "matching global channel"));
                return;
            }

            var deleted = await configurationService.DeleteConfigurationAsync("channel", isGlobal: true, cancellationToken);
            operations.Add(deleted
                ? CleanupOperation.Removed(configurationService.GetSettingsFilePath(isGlobal: true), "matching global channel")
                : CleanupOperation.Failed(configurationService.GetSettingsFilePath(isGlobal: true), "Matching global channel could not be deleted."));
        }
    }

    private DirectoryInfo GetHiveDirectory(string channel)
        => new(Path.Combine(executionContext.HivesDirectory.FullName, channel));

    // Reject channel names that contain path separators or `..` segments so a
    // crafted `--channel` argument cannot resolve outside the hives root and
    // recursively delete unrelated directories.
    private static void ValidateChannel(string channel)
    {
        if (string.IsNullOrWhiteSpace(channel) ||
            channel.Contains('/', StringComparison.Ordinal) ||
            channel.Contains('\\', StringComparison.Ordinal) ||
            channel == "." || channel == ".." ||
            channel.Contains("..", StringComparison.Ordinal))
        {
            throw new ArgumentException($"Invalid channel name '{channel}'. Channel names must not contain path separators or '..'.", nameof(channel));
        }
    }

    // Resolve the install layout root by walking up from HivesDirectory rather
    // than reading executionContext.AspireHomeDirectory directly. In production
    // those two paths agree (~/.aspire), but tests can configure HivesDirectory
    // independently from HomeDirectory; the layout root the script installer
    // wrote into is always the parent of `hives/`.
    private DirectoryInfo GetAspireHomeDirectory()
        => executionContext.HivesDirectory.Parent ?? executionContext.AspireHomeDirectory;

    private DirectoryInfo GetDogfoodDirectory(string channel)
        => new(Path.Combine(GetAspireHomeDirectory().FullName, "dogfood", channel));

    private DirectoryInfo GetSharedBinDirectory()
        => new(Path.Combine(GetAspireHomeDirectory().FullName, "bin"));

    private bool SharedInstallExists()
    {
        // Check the bin/sidecar targets and the bundle path independently so
        // a bundle-only remnant still triggers the "shared artifacts left in
        // place" hint even though EnumerateSharedBinTargets no longer yields
        // the bundle path.
        if (EnumerateSharedBinTargets().Any(t => t.Exists))
        {
            return true;
        }

        return GetBundleDirectory().Exists;
    }

    private IEnumerable<FileSystemInfo> EnumerateSharedBinTargets()
    {
        var binDirectory = GetSharedBinDirectory();
        var binaryPath = Path.Combine(binDirectory.FullName, OperatingSystem.IsWindows() ? "aspire.exe" : "aspire");
        yield return new FileInfo(binaryPath);
        yield return new FileInfo(Path.Combine(binDirectory.FullName, ".aspire-install.json"));
    }

    private DirectoryInfo GetBundleDirectory()
        => new(Path.Combine(GetAspireHomeDirectory().FullName, BundleDiscovery.BundleDirectoryName));

    // Returns true when the shared-install removal could not be carried out in
    // full (e.g. a leased bundle version was skipped). The caller promotes
    // that to a non-zero exit so automation can tell "fully removed" from
    // "removed everything except the leased bundle".
    private bool AddSharedInstallOperations(string? currentProcessPath, bool dryRun, List<CleanupOperation> operations)
    {
        // Resolve the bundle symlink target BEFORE deleting anything: once the
        // symlink is gone ResolveLinkTarget can no longer recover the
        // versions/<v>/ tree, and the leased-version guard below depends on
        // knowing the target path.
        var bundleDirectory = GetBundleDirectory();
        var bundleVersionResult = ResolveBundleVersionTarget(bundleDirectory);

        foreach (var target in EnumerateSharedBinTargets())
        {
            operations.Add(DeleteFileSystemInfoUnlessRunningFromTarget(target, currentProcessPath, dryRun));
        }

        switch (bundleVersionResult)
        {
            case BundleVersionTargetResult.Target target:
                // Skip both the symlink and the version directory when another
                // aspire process holds a lease on this version. Deleting only
                // the symlink would leave the lease holder unable to
                // re-resolve ~/.aspire/bundle even though the version itself
                // was preserved — silently defeating the lease guard.
                // Matches BundleService.TryCleanupStaleVersions which refuses
                // to touch leased entries.
                if (BundleVersionLease.HasActiveLease(target.Directory.FullName))
                {
                    operations.Add(CleanupOperation.Skipped(
                        bundleDirectory.FullName,
                        "Another running CLI / AppHost holds an active lease on the bundle version this link points at; leaving the link in place so the lease holder can still resolve it."));
                    operations.Add(CleanupOperation.Skipped(
                        target.Directory.FullName,
                        "Another running CLI / AppHost holds an active lease on this bundle version. Stop those processes and re-run cleanup."));
                    return true;
                }
                operations.Add(DeleteFileSystemInfoUnlessRunningFromTarget(bundleDirectory, currentProcessPath, dryRun));
                operations.Add(DeleteFileSystemInfoUnlessRunningFromTarget(target.Directory, currentProcessPath, dryRun));
                break;
            case BundleVersionTargetResult.ExternalLink:
                // Symlink resolves outside versions/ (e.g. a dev-mode redirect).
                // Removing just the symlink is safe; recursive delete on a
                // symlinked directory removes the link, not the target tree.
                operations.Add(DeleteFileSystemInfoUnlessRunningFromTarget(bundleDirectory, currentProcessPath, dryRun));
                break;
            case BundleVersionTargetResult.NotALinkOrMissing:
                // No symlink (or a real directory at ~/.aspire/bundle) — fall
                // through to recursive delete. The script installer always
                // creates a symlink, so a real directory here is an anomalous
                // state we still clean up; recursive delete on a missing path
                // is a no-op via the "does not exist" skip.
                operations.Add(DeleteFileSystemInfoUnlessRunningFromTarget(bundleDirectory, currentProcessPath, dryRun));
                break;
            case BundleVersionTargetResult.ResolveFailed failure:
                // Cannot tell where the symlink points — refuse to delete it,
                // because deleting it would silently strand whatever
                // versions/<v>/ tree it referenced.
                operations.Add(CleanupOperation.Failed(bundleDirectory.FullName, $"Could not resolve bundle link target to clean up versions/<v>/: {failure.Reason}"));
                break;
        }

        return false;
    }

    private BundleVersionTargetResult ResolveBundleVersionTarget(DirectoryInfo bundleDirectory)
    {
        try
        {
            var resolvedTarget = bundleDirectory.ResolveLinkTarget(returnFinalTarget: true);
            if (resolvedTarget is not DirectoryInfo targetDirectory)
            {
                // ResolveLinkTarget returns null when the path is not a reparse
                // point/symlink (including when the path doesn't exist). Lumping
                // these into one case is safe because both share the same
                // downstream handling: nothing to detach from.
                return new BundleVersionTargetResult.NotALinkOrMissing();
            }

            var versionsDirectory = new DirectoryInfo(Path.Combine(GetAspireHomeDirectory().FullName, BundleService.VersionsDirectoryName));
            if (!IsPathUnderTarget(targetDirectory.FullName, versionsDirectory.FullName))
            {
                return new BundleVersionTargetResult.ExternalLink();
            }

            return new BundleVersionTargetResult.Target(targetDirectory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Security.SecurityException)
        {
            return new BundleVersionTargetResult.ResolveFailed(ex.Message);
        }
    }

    private abstract record BundleVersionTargetResult
    {
        internal sealed record Target(DirectoryInfo Directory) : BundleVersionTargetResult;
        internal sealed record ExternalLink : BundleVersionTargetResult;
        internal sealed record NotALinkOrMissing : BundleVersionTargetResult;
        internal sealed record ResolveFailed(string Reason) : BundleVersionTargetResult;
    }

    private static bool IsPrChannel(string channel)
        => channel.StartsWith("pr-", StringComparison.Ordinal);

    private static bool IsSharedScriptChannel(string channel)
        => channel is "stable" or "staging" or "daily";

    private static bool IsIncludedInAll(string channel)
        => channel is "staging" or "daily" || IsPrChannel(channel);

    private static CleanupOperation DeleteDirectoryUnlessRunningFromTarget(DirectoryInfo directory, string? currentProcessPath, bool dryRun)
        => DeleteFileSystemInfoUnlessRunningFromTarget(directory, currentProcessPath, dryRun);

    private static CleanupOperation DeleteFileSystemInfoUnlessRunningFromTarget(FileSystemInfo target, string? currentProcessPath, bool dryRun)
    {
        if (!target.Exists)
        {
            return CleanupOperation.Skipped(target.FullName, "does not exist");
        }

        // Fail closed: if we cannot determine where the running CLI lives, do
        // not delete — a successful resolution is the only signal that we're
        // not removing ourselves out from under the current process.
        if (currentProcessPath is null)
        {
            return CleanupOperation.Failed(target.FullName, "Could not determine the running CLI path; refusing to delete cleanup targets. Re-run from a fully-resolved CLI invocation.");
        }

        if (IsPathUnderTarget(currentProcessPath, target.FullName))
        {
            return CleanupOperation.Failed(target.FullName, "The running CLI is inside this target. Re-run cleanup after this process exits or delete it manually.");
        }

        if (dryRun)
        {
            return CleanupOperation.WouldRemove(target.FullName);
        }

        try
        {
            switch (target)
            {
                case DirectoryInfo directory:
                    directory.Delete(recursive: true);
                    break;
                case FileInfo file:
                    file.Delete();
                    break;
            }

            return CleanupOperation.Removed(target.FullName);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return CleanupOperation.Failed(target.FullName, ex.Message);
        }
    }

    private static Task<CleanupOperation> DeleteDirectoryAsync(DirectoryInfo directory, string? currentProcessPath, bool dryRun, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(DeleteFileSystemInfoUnlessRunningFromTarget(directory, currentProcessPath, dryRun));
    }

    internal static bool IsPathUnderTarget(string path, string targetPath)
    {
        var normalizedPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var normalizedTarget = Path.TrimEndingDirectorySeparator(Path.GetFullPath(targetPath));

        return string.Equals(normalizedPath, normalizedTarget, s_pathComparison) ||
               normalizedPath.StartsWith(normalizedTarget + Path.DirectorySeparatorChar, s_pathComparison) ||
               normalizedPath.StartsWith(normalizedTarget + Path.AltDirectorySeparatorChar, s_pathComparison);
    }
}

internal sealed record CleanupResult(IReadOnlyList<CleanupOperation> Operations, bool HasFailures);

internal sealed record CleanupOperation(string Path, CleanupOperationStatus Status, string Reason)
{
    public static CleanupOperation Removed(string path, string reason = "") => new(path, CleanupOperationStatus.Removed, reason);
    public static CleanupOperation WouldRemove(string path, string reason = "") => new(path, CleanupOperationStatus.WouldRemove, reason);
    public static CleanupOperation Skipped(string path, string reason) => new(path, CleanupOperationStatus.Skipped, reason);
    public static CleanupOperation Failed(string path, string reason) => new(path, CleanupOperationStatus.Failed, reason);
}

internal enum CleanupOperationStatus
{
    Removed,
    WouldRemove,
    Skipped,
    Failed
}
