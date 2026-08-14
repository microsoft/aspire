// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using Aspire.Shared.TerminalHost;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.Lifecycle;

internal sealed class TerminalHostOrphanCleanupService(
    ILogger<TerminalHostOrphanCleanupService> logger,
    IHostApplicationLifetime applicationLifetime) : IAsyncDisposable
{
    private readonly object _sync = new();
    private Task? _cleanupTask;

    internal Task Completion
    {
        get
        {
            lock (_sync)
            {
                return _cleanupTask ?? Task.CompletedTask;
            }
        }
    }

    internal int StartCount { get; private set; }

    internal void Start(string trmnlDirectory)
    {
        ArgumentException.ThrowIfNullOrEmpty(trmnlDirectory);

        lock (_sync)
        {
            if (_cleanupTask is not null)
            {
                return;
            }

            StartCount++;
            _cleanupTask = Task.Run(
                () => SweepAsync(trmnlDirectory, logger, applicationLifetime.ApplicationStopping),
                CancellationToken.None);
        }
    }

    public async ValueTask DisposeAsync()
    {
        Task? cleanupTask;
        lock (_sync)
        {
            cleanupTask = _cleanupTask;
        }

        if (cleanupTask is not null)
        {
            await cleanupTask.ConfigureAwait(false);
        }
    }

    internal static bool DeleteReplicaFiles(string trmnlDirectory, string replicaId, ILogger? logger)
    {
        var socketPaths = new[]
        {
            TerminalHostPaths.GetSocketPath(trmnlDirectory, replicaId, TerminalHostPaths.ProducerSockPurpose),
            TerminalHostPaths.GetSocketPath(trmnlDirectory, replicaId, TerminalHostPaths.ConsumerSockPurpose),
            TerminalHostPaths.GetSocketPath(trmnlDirectory, replicaId, TerminalHostPaths.ControlSockPurpose),
        };

        var allSocketsDeleted = true;
        foreach (var socketPath in socketPaths)
        {
            try
            {
                File.Delete(socketPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                allSocketsDeleted = false;
                logger?.LogWarning(ex, "Failed to delete terminal socket '{Path}'.", socketPath);
            }
        }

        if (!allSocketsDeleted)
        {
            logger?.LogWarning(
                "Keeping terminal metadata for replica '{ReplicaId}' because one or more socket artifacts could not be removed.",
                replicaId);
            return false;
        }

        var metadataPath = TerminalHostPaths.GetMetadataPath(trmnlDirectory, replicaId);
        try
        {
            File.Delete(metadataPath);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger?.LogWarning(ex, "Failed to delete terminal metadata '{Path}'.", metadataPath);
            return false;
        }
    }

    internal static string GetCurrentProcessScopeId()
    {
        if (OperatingSystem.IsLinux())
        {
            // Linux exposes the PID namespace as a link such as:
            //   /proc/self/ns/pid -> pid:[4026531836]
            // PIDs from another container namespace are not comparable with ours. Include stable
            // machine identity as well so a shared home on another machine cannot look local.
            var machineScope = TryReadTrimmedText("/etc/machine-id")
                ?? TryReadTrimmedText("/var/lib/dbus/machine-id")
                ?? $"name:{Environment.MachineName}";
            var pidNamespace = TryGetLinkTarget("/proc/self/ns/pid")
                ?? $"unresolved:{Environment.ProcessId}";
            return $"linux:{machineScope}:{Environment.MachineName}:pidns:{pidNamespace}";
        }

        return $"machine:{Environment.MachineName}";
    }

    internal static string? GetCurrentBootId()
        => OperatingSystem.IsLinux()
            ? TryReadTrimmedText("/proc/sys/kernel/random/boot_id")
            : null;

    private static async Task SweepAsync(
        string trmnlDirectory,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        try
        {
            if (!Directory.Exists(trmnlDirectory))
            {
                return;
            }

            var currentScopeId = GetCurrentProcessScopeId();
            var currentBootId = GetCurrentBootId();
            foreach (var candidatePath in Directory.GetFiles(trmnlDirectory, $"*.{TerminalHostPaths.MetadataSuffix}"))
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (!TerminalHostPaths.TryGetReplicaIdFromMetadataPath(candidatePath, out var replicaId))
                {
                    continue;
                }

                if (!TryAcquireLegacyLock(trmnlDirectory, replicaId, logger, out var legacyLock))
                {
                    continue;
                }

                using var heldLegacyLock = legacyLock;

                TerminalHostMetadata? metadata;
                try
                {
                    // Sidecars are tiny UTF-8 JSON documents. Schema v1 did not contain a stable
                    // process identity or scope; those properties intentionally deserialize as null.
                    // Allow exact-path shutdown cleanup to delete a sidecar while this background
                    // reader is inspecting it; otherwise a fast AppHost stop can lose that race on
                    // Windows and leave metadata behind.
                    var stream = new FileStream(
                        candidatePath,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.ReadWrite | FileShare.Delete,
                        bufferSize: 4096,
                        useAsync: true);
                    await using (stream.ConfigureAwait(false))
                    {
                        metadata = await JsonSerializer.DeserializeAsync<TerminalHostMetadata>(
                            stream,
                            cancellationToken: cancellationToken).ConfigureAwait(false);
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
                {
                    logger.LogWarning(ex, "Unable to inspect terminal metadata '{Path}'; leaving its artifacts in place.", candidatePath);
                    continue;
                }

                if (metadata is null
                    || !string.Equals(metadata.ReplicaId, replicaId, StringComparison.Ordinal)
                    || metadata.AppHostPid <= 0)
                {
                    logger.LogWarning("Terminal metadata '{Path}' is invalid; leaving its artifacts in place.", candidatePath);
                    continue;
                }

                var unableToInspectOwner = false;
                bool ownerIsRunning;
                if (metadata.SchemaVersion == 1)
                {
                    // Released schema-v1 sidecars have only a PID. Preserve compatibility with a
                    // conservative existence check that assumes inaccessible processes are alive.
                    ownerIsRunning = ProcessStartTimeHelper.IsProcessRunning(
                        metadata.AppHostPid,
                        expectedStartTimeUnixMilliseconds: null,
                        tolerance: null,
                        assumeRunningWhenUnableToInspect: true,
                        unableToInspect: out unableToInspectOwner);
                }
                else if (metadata.SchemaVersion == 2
                    && metadata.SchemaV2AppHostProcessIdentity is > 0)
                {
                    // Schema v2 was emitted by earlier preview builds of this feature. It has the
                    // stable process identity but predates machine/PID-namespace and boot scoping.
                    ownerIsRunning = ProcessStartTimeHelper.IsProcessRunning(
                        metadata.AppHostPid,
                        metadata.SchemaV2AppHostProcessIdentity,
                        tolerance: null,
                        assumeRunningWhenUnableToInspect: true,
                        unableToInspect: out unableToInspectOwner);
                }
                else if (metadata.SchemaVersion == TerminalHostMetadata.CurrentSchemaVersion
                    && metadata.AppHostProcessIdentity is > 0
                    && !string.IsNullOrEmpty(metadata.AppHostProcessScopeId))
                {
                    if (!string.Equals(metadata.AppHostProcessScopeId, currentScopeId, StringComparison.Ordinal))
                    {
                        logger.LogDebug(
                            "Skipping terminal replica '{ReplicaId}' because its owner is in process scope '{OwnerScopeId}', not '{CurrentScopeId}'.",
                            replicaId,
                            metadata.AppHostProcessScopeId,
                            currentScopeId);
                        continue;
                    }

                    ownerIsRunning = metadata.AppHostBootId is not null
                        && currentBootId is not null
                        && !string.Equals(metadata.AppHostBootId, currentBootId, StringComparison.Ordinal)
                        ? false
                        : ProcessStartTimeHelper.IsProcessRunning(
                            metadata.AppHostPid,
                            metadata.AppHostProcessIdentity,
                            tolerance: null,
                            assumeRunningWhenUnableToInspect: true,
                            unableToInspect: out unableToInspectOwner);
                }
                else
                {
                    logger.LogWarning(
                        "Skipping terminal metadata '{Path}' with unsupported schema version {SchemaVersion}.",
                        candidatePath,
                        metadata.SchemaVersion);
                    continue;
                }

                if (unableToInspectOwner)
                {
                    logger.LogWarning(
                        "Unable to verify the owner of terminal replica '{ReplicaId}' (AppHost PID {AppHostPid}); leaving its artifacts in place.",
                        replicaId,
                        metadata.AppHostPid);
                    continue;
                }

                if (ownerIsRunning)
                {
                    continue;
                }

                logger.LogInformation(
                    "Reclaiming orphaned terminal artifacts for replica '{ReplicaId}' owned by AppHost PID {AppHostPid}.",
                    replicaId,
                    metadata.AppHostPid);
                DeleteReplicaFiles(trmnlDirectory, replicaId, logger);
            }

            CleanupLegacyLocks(trmnlDirectory, logger, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Failed to sweep orphaned terminal artifacts in '{Directory}'.", trmnlDirectory);
        }
    }

    private static void CleanupLegacyLocks(string trmnlDirectory, ILogger logger, CancellationToken cancellationToken)
    {
        foreach (var lockPath in Directory.GetFiles(trmnlDirectory, $"*.{TerminalHostPaths.LockSuffix}"))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!TerminalHostPaths.TryGetReplicaIdFromLockPath(lockPath, out var replicaId)
                || ReplicaArtifactsExist(trmnlDirectory, replicaId))
            {
                continue;
            }

            try
            {
                // Earlier PR builds serialized replica operations through persistent .lock files.
                // Hold the exact inode exclusively while deleting its directory entry so another
                // process cannot swap in a new lock between acquisition and deletion.
                using var stream = new FileStream(lockPath, FileMode.Open, FileAccess.ReadWrite, FileShare.Delete);
                if (ReplicaArtifactsExist(trmnlDirectory, replicaId))
                {
                    continue;
                }

                File.Delete(lockPath);
            }
            catch (FileNotFoundException)
            {
            }
            catch (IOException)
            {
                // An earlier build still owns the lock. Its artifacts will be reconsidered later.
            }
            catch (UnauthorizedAccessException ex)
            {
                logger.LogWarning(ex, "Unable to reclaim legacy terminal lock '{Path}'.", lockPath);
            }
        }
    }

    private static bool TryAcquireLegacyLock(
        string trmnlDirectory,
        string replicaId,
        ILogger logger,
        out FileStream? legacyLock)
    {
        var lockPath = Path.Combine(trmnlDirectory, $"{replicaId}.{TerminalHostPaths.LockSuffix}");
        if (!File.Exists(lockPath))
        {
            legacyLock = null;
            return true;
        }

        try
        {
            // Schema-v2 preview builds use this persistent lock around metadata replacement and cleanup.
            // Deny read/write sharing so those builds cannot replace deterministic paths while this sweep
            // inspects them; FileShare.Delete lets stale-lock collection remove this exact inode later.
            legacyLock = new FileStream(lockPath, FileMode.Open, FileAccess.ReadWrite, FileShare.Delete);
            return true;
        }
        catch (FileNotFoundException)
        {
            legacyLock = null;
            return false;
        }
        catch (IOException)
        {
            logger.LogDebug(
                "Skipping terminal replica '{ReplicaId}' because an earlier build is updating it.",
                replicaId);
            legacyLock = null;
            return false;
        }
        catch (UnauthorizedAccessException ex)
        {
            logger.LogWarning(ex, "Unable to acquire legacy terminal lock '{Path}'; leaving its artifacts in place.", lockPath);
            legacyLock = null;
            return false;
        }
    }

    private static bool ReplicaArtifactsExist(string trmnlDirectory, string replicaId)
        => File.Exists(TerminalHostPaths.GetMetadataPath(trmnlDirectory, replicaId))
            || File.Exists(TerminalHostPaths.GetSocketPath(trmnlDirectory, replicaId, TerminalHostPaths.ProducerSockPurpose))
            || File.Exists(TerminalHostPaths.GetSocketPath(trmnlDirectory, replicaId, TerminalHostPaths.ConsumerSockPurpose))
            || File.Exists(TerminalHostPaths.GetSocketPath(trmnlDirectory, replicaId, TerminalHostPaths.ControlSockPurpose));

    private static string? TryReadTrimmedText(string path)
    {
        try
        {
            var value = File.ReadAllText(path).Trim();
            return value.Length > 0 ? value : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string? TryGetLinkTarget(string path)
    {
        try
        {
            return new FileInfo(path).LinkTarget;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
