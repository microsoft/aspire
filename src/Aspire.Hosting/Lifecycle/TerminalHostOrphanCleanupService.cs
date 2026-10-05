// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Eventing;
using Aspire.Shared.TerminalHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.Lifecycle;

internal sealed class TerminalHostOrphanCleanupService : IAsyncDisposable
{
    private static readonly TimeSpan s_shutdownCleanupTimeout = TimeSpan.FromSeconds(2);
    private readonly object _sync = new();
    private readonly List<(string TrmnlDirectory, string[] ReplicaIds)> _registeredReplicaArtifacts = [];
    private readonly ILogger<TerminalHostOrphanCleanupService> _logger;
    private readonly IHostApplicationLifetime _applicationLifetime;
    private readonly CancellationTokenRegistration _applicationStoppedRegistration;
    private Task? _cleanupTask;

    public TerminalHostOrphanCleanupService(
        ILogger<TerminalHostOrphanCleanupService> logger,
        IHostApplicationLifetime applicationLifetime)
    {
        _logger = logger;
        _applicationLifetime = applicationLifetime;
        _applicationStoppedRegistration =
            applicationLifetime.ApplicationStopped.Register(DeleteRegisteredReplicaFiles);
    }

    internal static TimeSpan InvalidMetadataRetentionPeriod { get; } = TimeSpan.FromDays(7);

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

    internal Task SubscribeAsync(
        IDistributedApplicationEventing eventing,
        DistributedApplicationExecutionContext executionContext,
        CancellationToken _)
    {
        ArgumentNullException.ThrowIfNull(eventing);

        if (executionContext.IsRunMode)
        {
            // DI subscribers attach after builder-phase WithTerminal handlers. Starting the
            // sweep here prevents it from observing another resource's sidecar mid-write.
            eventing.Subscribe<BeforeStartEvent>((@event, _) =>
            {
                var configuration = @event.Services.GetRequiredService<IConfiguration>();
                var trmnlDirectory = configuration[TerminalHostPaths.DirectoryOverrideConfigName];
                if (string.IsNullOrEmpty(trmnlDirectory))
                {
                    var homeDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                    trmnlDirectory = TerminalHostPaths.GetTrmnlDirectory(homeDirectory);
                }

                Start(trmnlDirectory);
                return Task.CompletedTask;
            });
        }

        return Task.CompletedTask;
    }

    internal void RegisterReplicaArtifacts(string trmnlDirectory, IReadOnlyList<string> replicaIds)
    {
        ArgumentException.ThrowIfNullOrEmpty(trmnlDirectory);
        ArgumentNullException.ThrowIfNull(replicaIds);

        lock (_sync)
        {
            _registeredReplicaArtifacts.Add((trmnlDirectory, [.. replicaIds]));
        }
    }

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
                () => SweepAsync(trmnlDirectory, _logger, _applicationLifetime.ApplicationStopping),
                CancellationToken.None);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _applicationStoppedRegistration.Dispose();

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

    private void DeleteRegisteredReplicaFiles()
    {
        (string TrmnlDirectory, string[] ReplicaIds)[] registeredReplicaArtifacts;
        lock (_sync)
        {
            registeredReplicaArtifacts = [.. _registeredReplicaArtifacts];
        }

        // ApplicationStopped callbacks run synchronously. Use one app-wide wait budget so
        // filesystem stalls cannot multiply the shutdown delay across terminal resources.
        var cleanupTask = Task.Run(() =>
        {
            foreach (var (trmnlDirectory, replicaIds) in registeredReplicaArtifacts)
            {
                foreach (var replicaId in replicaIds)
                {
                    DeleteReplicaFiles(trmnlDirectory, replicaId, _logger);
                }
            }
        }, CancellationToken.None);

        try
        {
            cleanupTask.WaitAsync(s_shutdownCleanupTimeout).GetAwaiter().GetResult();
        }
        catch (TimeoutException)
        {
            _logger.LogWarning(
                "Timed out after {TimeoutSeconds} seconds while deleting terminal artifacts during shutdown. The next AppHost startup will retry cleanup.",
                s_shutdownCleanupTimeout.TotalSeconds);
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
                catch (JsonException ex)
                {
                    logger.LogWarning(
                        ex,
                        "Unable to inspect terminal metadata '{Path}'; it is eligible for cleanup after {RetentionDays} days only when no socket artifacts exist.",
                        candidatePath,
                        InvalidMetadataRetentionPeriod.TotalDays);
                    ReclaimExpiredInvalidMetadata(candidatePath, trmnlDirectory, replicaId, logger);
                    continue;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    logger.LogWarning(ex, "Unable to inspect terminal metadata '{Path}'; leaving its artifacts in place.", candidatePath);
                    continue;
                }

                if (metadata is null)
                {
                    logger.LogWarning(
                        "Terminal metadata '{Path}' is invalid; it is eligible for cleanup after {RetentionDays} days only when no socket artifacts exist.",
                        candidatePath,
                        InvalidMetadataRetentionPeriod.TotalDays);
                    ReclaimExpiredInvalidMetadata(candidatePath, trmnlDirectory, replicaId, logger);
                    continue;
                }

                if (metadata.SchemaVersion != 1
                    && metadata.SchemaVersion != 2
                    && metadata.SchemaVersion != TerminalHostMetadata.CurrentSchemaVersion)
                {
                    // A newer Aspire build may own this replica. Its age alone is not enough
                    // evidence for an older build to delete an unknown schema's live sockets.
                    logger.LogWarning(
                        "Skipping terminal metadata '{Path}' with unsupported schema version {SchemaVersion}.",
                        candidatePath,
                        metadata.SchemaVersion);
                    continue;
                }

                if (metadata.SchemaVersion is 1 or 2)
                {
                    // Schema v1 and the PR-preview schema v2 predate machine/PID-namespace
                    // scoping. A missing PID in this namespace cannot prove that an owner using a
                    // shared home directory is dead, so preserve these artifacts unconditionally.
                    logger.LogDebug(
                        "Skipping unscoped schema-v{SchemaVersion} terminal metadata '{Path}'.",
                        metadata.SchemaVersion,
                        candidatePath);
                    continue;
                }

                if (!string.Equals(metadata.ReplicaId, replicaId, StringComparison.Ordinal)
                    || metadata.AppHostPid <= 0
                    || metadata.AppHostProcessIdentity is not > 0
                    || string.IsNullOrEmpty(metadata.AppHostProcessScopeId))
                {
                    logger.LogWarning(
                        "Terminal metadata '{Path}' is invalid; it is eligible for cleanup after {RetentionDays} days only when no socket artifacts exist.",
                        candidatePath,
                        InvalidMetadataRetentionPeriod.TotalDays);
                    ReclaimExpiredInvalidMetadata(candidatePath, trmnlDirectory, replicaId, logger);
                    continue;
                }

                var unableToInspectOwner = false;
                if (!string.Equals(metadata.AppHostProcessScopeId, currentScopeId, StringComparison.Ordinal))
                {
                    logger.LogDebug(
                        "Skipping terminal replica '{ReplicaId}' because its owner is in process scope '{OwnerScopeId}', not '{CurrentScopeId}'.",
                        replicaId,
                        metadata.AppHostProcessScopeId,
                        currentScopeId);
                    continue;
                }

                var ownerIsRunning = metadata.AppHostBootId is not null
                    && currentBootId is not null
                    && !string.Equals(metadata.AppHostBootId, currentBootId, StringComparison.Ordinal)
                    ? false
                    : ProcessStartTimeHelper.IsProcessRunning(
                        metadata.AppHostPid,
                        metadata.AppHostProcessIdentity,
                        tolerance: null,
                        assumeRunningWhenUnableToInspect: true,
                        unableToInspect: out unableToInspectOwner);

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

            CleanupExpiredMetadataTemporaryFiles(trmnlDirectory, logger, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Failed to sweep orphaned terminal artifacts in '{Directory}'.", trmnlDirectory);
        }
    }

    private static void ReclaimExpiredInvalidMetadata(
        string metadataPath,
        string trmnlDirectory,
        string replicaId,
        ILogger logger)
    {
        DateTime lastWriteTimeUtc;
        try
        {
            lastWriteTimeUtc = File.GetLastWriteTimeUtc(metadataPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Unable to inspect the age of invalid terminal metadata '{Path}'.", metadataPath);
            return;
        }

        if (lastWriteTimeUtc > DateTime.UtcNow - InvalidMetadataRetentionPeriod)
        {
            return;
        }

        if (ReplicaSocketArtifactsExist(trmnlDirectory, replicaId))
        {
            logger.LogWarning(
                "Expired invalid terminal metadata '{Path}' still has socket artifacts; preserving the replica because its owner cannot be verified.",
                metadataPath);
            return;
        }

        try
        {
            File.Delete(metadataPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Unable to reclaim expired invalid terminal metadata '{Path}'.", metadataPath);
            return;
        }

        logger.LogInformation(
            "Reclaimed expired invalid terminal metadata '{Path}' for socket-free replica '{ReplicaId}'; it was last modified at {LastWriteTimeUtc}.",
            metadataPath,
            replicaId,
            lastWriteTimeUtc);
    }

    private static void CleanupExpiredMetadataTemporaryFiles(
        string trmnlDirectory,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        foreach (var temporaryPath in Directory.GetFiles(trmnlDirectory, $"*.{TerminalHostPaths.MetadataTemporarySuffix}"))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!TerminalHostPaths.TryGetReplicaIdFromMetadataTemporaryPath(temporaryPath, out var replicaId))
            {
                continue;
            }

            DateTime lastWriteTimeUtc;
            try
            {
                lastWriteTimeUtc = File.GetLastWriteTimeUtc(temporaryPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(ex, "Unable to inspect temporary terminal metadata '{Path}'.", temporaryPath);
                continue;
            }

            if (lastWriteTimeUtc > DateTime.UtcNow - InvalidMetadataRetentionPeriod)
            {
                continue;
            }

            try
            {
                File.Delete(temporaryPath);
                logger.LogInformation(
                    "Reclaimed stale temporary terminal metadata '{Path}' for replica '{ReplicaId}'.",
                    temporaryPath,
                    replicaId);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(ex, "Unable to reclaim stale temporary terminal metadata '{Path}'.", temporaryPath);
            }
        }
    }

    private static bool ReplicaSocketArtifactsExist(string trmnlDirectory, string replicaId)
        => File.Exists(TerminalHostPaths.GetSocketPath(trmnlDirectory, replicaId, TerminalHostPaths.ProducerSockPurpose))
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

internal sealed class TerminalHostOrphanCleanupEventingSubscriber(
    TerminalHostOrphanCleanupService cleanupService) : IDistributedApplicationEventingSubscriber
{
    public Task SubscribeAsync(
        IDistributedApplicationEventing eventing,
        DistributedApplicationExecutionContext executionContext,
        CancellationToken cancellationToken)
        => cleanupService.SubscribeAsync(eventing, executionContext, cancellationToken);
}
