// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using System.Threading.Channels;
using Aspire.Cli.Processes;
using Aspire.Shared;
using Microsoft.Extensions.Logging;

namespace Aspire.Cli.Backchannel;

/// <summary>
/// Produces the experimental tray snapshot stream over the command's stdout pipe.
/// </summary>
internal sealed class TrayWatchStream(
    IAuxiliaryBackchannelMonitor monitor,
    IProcessIdentityProvider processIdentityProvider,
    TimeProvider timeProvider,
    ILogger logger)
{
    internal static TimeSpan DashboardLookupTimeout { get; } = TimeSpan.FromSeconds(2);
    internal static TimeSpan DashboardSnapshotTimeout { get; } = TimeSpan.FromSeconds(5);
    internal static TimeSpan DashboardRetryInterval { get; } = TimeSpan.FromSeconds(5);
    internal const int MaximumConcurrentDashboardLookups = 8;

    private static readonly Task s_never = new TaskCompletionSource().Task;

    private readonly HashSet<IAppHostAuxiliaryBackchannel> _pendingDashboardLookups = new(ReferenceEqualityComparer.Instance);

    // Completed lookups per live connection, including AppHosts that reported no dashboard.
    // Only the snapshot producer reads or writes this, so it needs no synchronization.
    private readonly Dictionary<IAppHostAuxiliaryBackchannel, string?> _dashboardUrls = new(ReferenceEqualityComparer.Instance);

    public async Task<int> RunAsync(Func<string, CancellationToken, Task> writeLineAsync, CancellationToken cancellationToken)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = lifetime.Token;
        await using var connections = monitor.WatchConnectionsAsync(token, readOnly: true).GetAsyncEnumerator(token);
        var updates = Channel.CreateBounded<Update>(new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = true
        });
        var healthChanges = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = true
        });
        var healthSubscriptions = new Dictionary<IAppHostAuxiliaryBackchannel, TrayResourceHealthSubscription>(ReferenceEqualityComparer.Instance);
        List<(IAppHostAuxiliaryBackchannel Connection, TrayAppHost Host)> candidates = [];
        TrayAppHost[] hosts = [];
        Task producer = Task.CompletedTask;
        Task<bool>? pendingConnectionsRead = null;
        Task<bool>? pendingHealthRead = null;
        Task<bool>? pendingRead = null;
        Task<bool>? pendingHeartbeat = null;
        Task? pendingDashboardRetry = null;
        var terminalError = false;
        try
        {
            // Initial discovery is an explicit state, including zero hosts. Do not allow the
            // latest-wins queue or a heartbeat to replace this first snapshot.
            var initial = await ReadUpdateAsync().ConfigureAwait(false);
            terminalError = initial.IsError;
            await writeLineAsync(initial.Json, token).ConfigureAwait(false);
            if (initial.IsError)
            {
                return CliExitCodes.FailedToFindProject;
            }

            using var heartbeat = new PeriodicTimer(TrayCliProtocol.HeartbeatInterval, timeProvider);
            pendingHeartbeat = heartbeat.WaitForNextTickAsync(token).AsTask();
            producer = ProduceAsync(initial.Json);
            pendingRead = updates.Reader.WaitToReadAsync(token).AsTask();
            var heartbeatJson = JsonSerializer.Serialize(new TrayWatchMessage
            {
                Version = TrayCliProtocol.Version,
                Type = "heartbeat"
            }, TrayCliJsonContext.Default.TrayWatchMessage);

            while (true)
            {
                await Task.WhenAny(pendingRead, pendingHeartbeat).ConfigureAwait(false);
                if (pendingRead.IsCompleted)
                {
                    if (!await pendingRead.ConfigureAwait(false))
                    {
                        return CliExitCodes.FailedToFindProject;
                    }

                    if (updates.Reader.TryRead(out var update))
                    {
                        terminalError = update.IsError;
                        await writeLineAsync(update.Json, token).ConfigureAwait(false);
                        if (update.IsError)
                        {
                            return CliExitCodes.FailedToFindProject;
                        }
                    }
                    pendingRead = updates.Reader.WaitToReadAsync(token).AsTask();
                }

                if (pendingHeartbeat.IsCompleted)
                {
                    if (!await pendingHeartbeat.ConfigureAwait(false))
                    {
                        return CliExitCodes.Success;
                    }

                    // A write on an otherwise quiet stream detects a closed consumer. Heartbeats
                    // are never queued with snapshots, where they could evict the latest state.
                    await writeLineAsync(heartbeatJson, token).ConfigureAwait(false);
                    pendingHeartbeat = heartbeat.WaitForNextTickAsync(token).AsTask();
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            return CliExitCodes.Success;
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            logger.LogDebug(ex, "The tray protocol output pipe closed.");
            // Consumer disconnect is normal completion, as in legacy ps --follow. Preserve
            // an already-discovered terminal error if its final output write also fails.
            return terminalError ? CliExitCodes.FailedToFindProject : CliExitCodes.Success;
        }
        finally
        {
            await lifetime.CancelAsync().ConfigureAwait(false);
            try
            {
                // Observe every outstanding operation before disposing the enumerator. In
                // particular, never race its DisposeAsync against the producer's MoveNextAsync.
                await Task.WhenAll(producer, pendingRead ?? Task.CompletedTask, pendingHeartbeat ?? Task.CompletedTask).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
            }
            try
            {
                await Task.WhenAll(
                    pendingConnectionsRead ?? Task.CompletedTask,
                    pendingHealthRead ?? Task.CompletedTask,
                    pendingDashboardRetry ?? Task.CompletedTask).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
            }
            await Task.WhenAll(healthSubscriptions.Values.Select(subscription => subscription.DisposeAsync().AsTask())).ConfigureAwait(false);
        }

        async Task ProduceAsync(string lastSnapshot)
        {
            try
            {
                while (!token.IsCancellationRequested)
                {
                    var update = await ReadUpdateAsync().ConfigureAwait(false);
                    if (update.IsError || !string.Equals(lastSnapshot, update.Json, StringComparison.Ordinal))
                    {
                        updates.Writer.TryWrite(update);
                        lastSnapshot = update.Json;
                    }
                    if (update.IsError)
                    {
                        return;
                    }
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
            }
            finally
            {
                updates.Writer.TryComplete();
            }
        }

        async Task<Update> ReadUpdateAsync()
        {
            try
            {
                pendingConnectionsRead ??= connections.MoveNextAsync().AsTask();
                if (pendingHealthRead is not null)
                {
                    await Task.WhenAny(pendingConnectionsRead, pendingHealthRead, pendingDashboardRetry ?? s_never).ConfigureAwait(false);
                }

                var enrichDashboardUrls = false;
                if (pendingHealthRead is null || pendingConnectionsRead.IsCompleted)
                {
                    var connectionRead = pendingConnectionsRead;
                    pendingConnectionsRead = null;
                    if (!await connectionRead.ConfigureAwait(false))
                    {
                        return Error("discovery_failed");
                    }

                    candidates = [];
                    foreach (var connection in connections.Current)
                    {
                        if (connection.AppHostInfo is not { } info)
                        {
                            continue;
                        }
                        if (candidates.Count == TrayCliProtocol.MaximumAppHosts)
                        {
                            return Error("limit_exceeded");
                        }

                        var startedAt = processIdentityProvider.GetStartTimeUnixMilliseconds(info.ProcessId);
                        candidates.Add((connection, new TrayAppHost
                        {
                            AppHostPath = info.AppHostPath,
                            AppHostPid = info.ProcessId,
                            ProcessStartTimeUnixMilliseconds = startedAt is > 0 ? startedAt : null
                        }));
                    }

                    var currentConnections = new Dictionary<IAppHostAuxiliaryBackchannel, TrayAppHost>(ReferenceEqualityComparer.Instance);
                    foreach (var (connection, host) in candidates)
                    {
                        currentConnections.Add(connection, host);
                    }
                    foreach (var (connection, subscription) in healthSubscriptions.ToArray())
                    {
                        if (!currentConnections.TryGetValue(connection, out var host) || subscription.Host != host)
                        {
                            await subscription.DisposeAsync().ConfigureAwait(false);
                            healthSubscriptions.Remove(connection);
                        }
                    }
                    foreach (var (connection, host) in candidates)
                    {
                        if (!healthSubscriptions.ContainsKey(connection))
                        {
                            healthSubscriptions.Add(connection, new TrayResourceHealthSubscription(
                                connection, host, () => healthChanges.Writer.TryWrite(true), logger, token));
                        }
                    }

                    foreach (var connection in _dashboardUrls.Keys.ToArray())
                    {
                        if (!currentConnections.ContainsKey(connection))
                        {
                            _dashboardUrls.Remove(connection);
                        }
                    }
                    enrichDashboardUrls = true;
                }

                if (pendingDashboardRetry is { IsCompleted: true })
                {
                    await pendingDashboardRetry.ConfigureAwait(false);
                    pendingDashboardRetry = null;
                    enrichDashboardUrls = true;
                }

                // Health-only publications reuse the last URL enrichment instead of issuing
                // optional dashboard RPCs for every resource transition.
                if (enrichDashboardUrls)
                {
                    hosts = await EnrichDashboardUrlsAsync(candidates, token).ConfigureAwait(false);
                }

                // The AppHost answers dashboard lookups only once its dashboard is healthy, which
                // can take longer than the lookup deadline for a freshly started AppHost. Discovery
                // does not republish an unchanged connection set, so schedule a retry for lookups
                // that timed out or were skipped; otherwise the URL would never appear.
                if (pendingDashboardRetry is null && candidates.Any(candidate => !_dashboardUrls.ContainsKey(candidate.Connection)))
                {
                    pendingDashboardRetry = Task.Delay(DashboardRetryInterval, timeProvider, token);
                }

                if (pendingHealthRead is not null && pendingHealthRead.IsCompleted)
                {
                    await pendingHealthRead.ConfigureAwait(false);
                    pendingHealthRead = null;
                }
                healthChanges.Reader.TryRead(out _);
                pendingHealthRead ??= healthChanges.Reader.WaitToReadAsync(token).AsTask();

                var message = new TrayWatchMessage
                {
                    Version = TrayCliProtocol.Version,
                    Type = "snapshot",
                    AppHosts = hosts.Select((host, index) => host with { Health = healthSubscriptions[candidates[index].Connection].Health })
                        .OrderBy(host => host.AppHostPath, StringComparer.Ordinal)
                        .ThenBy(host => host.AppHostPid)
                        .ThenBy(host => host.ProcessStartTimeUnixMilliseconds)
                        .ThenBy(host => host.DashboardUrl, StringComparer.Ordinal)
                        .ToArray()
                };
                var json = JsonSerializer.Serialize(message, TrayCliJsonContext.Default.TrayWatchMessage);
                return json.Length > TrayCliProtocol.MaximumMessageLength ? Error("limit_exceeded") : new Update(json, false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Tray protocol AppHost discovery failed.");
                return Error("discovery_failed");
            }
        }
    }

    private async Task<TrayAppHost[]> EnrichDashboardUrlsAsync(
        List<(IAppHostAuxiliaryBackchannel Connection, TrayAppHost Host)> candidates,
        CancellationToken cancellationToken)
    {
        var hosts = new TrayAppHost[candidates.Count];
        var resolved = new bool[candidates.Count];
        for (var i = 0; i < candidates.Count; i++)
        {
            var (connection, host) = candidates[i];
            resolved[i] = _dashboardUrls.TryGetValue(connection, out var cachedUrl);
            hosts[i] = resolved[i] ? host with { DashboardUrl = cachedUrl } : host;
        }

        // Per-call deadlines alone still scale with the number of unresponsive peers.
        // Keep the entire optional enrichment well below the protocol's 30-second liveness
        // budget, including the initial snapshot, without issuing unbounded concurrent RPCs.
        using var snapshotTimeout = new CancellationTokenSource(DashboardSnapshotTimeout, timeProvider);
        await Parallel.ForEachAsync(Enumerable.Range(0, hosts.Length), new ParallelOptions
        {
            MaxDegreeOfParallelism = MaximumConcurrentDashboardLookups,
            CancellationToken = cancellationToken
        }, async (index, token) =>
        {
            var connection = candidates[index].Connection;
            if (resolved[index] || snapshotTimeout.IsCancellationRequested || !TryBeginDashboardLookup(connection))
            {
                return;
            }

            using var lookupTimeout = new CancellationTokenSource(DashboardLookupTimeout, timeProvider);
            using var lookupCancellation = CancellationTokenSource.CreateLinkedTokenSource(token, snapshotTimeout.Token, lookupTimeout.Token);
            Task<DashboardUrlsState?>? pendingLookup = null;
            try
            {
                lookupCancellation.Token.ThrowIfCancellationRequested();
                pendingLookup = connection.GetDashboardUrlsAsync(lookupCancellation.Token);
                var urls = await pendingLookup.WaitAsync(lookupCancellation.Token).ConfigureAwait(false);
                // Each worker owns one array element; only completed results reach serialization.
                hosts[index] = hosts[index] with { DashboardUrl = urls?.BaseUrlWithLoginToken };
                resolved[index] = true;
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                logger.LogDebug("Dashboard URL lookup timed out or was canceled for AppHost PID {Pid}.", hosts[index].AppHostPid);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A faulted RPC is not a dashboard that is still starting; retrying it on every
                // interval would only repeat the failure, so record the URL as unavailable.
                logger.LogDebug(ex, "Dashboard URL unavailable for AppHost PID {Pid}.", hosts[index].AppHostPid);
                resolved[index] = true;
            }
            finally
            {
                if (pendingLookup is null)
                {
                    CompleteDashboardLookup(connection);
                }
                else
                {
                    // Local cancellation must not wait for the remote peer to acknowledge it.
                    // Retain its slot until the actual RPC completes, so later snapshots cannot
                    // accumulate abandoned requests. Observe late faults without disconnecting.
                    _ = pendingLookup.ContinueWith(
                        task =>
                        {
                            _ = task.Exception;
                            CompleteDashboardLookup(connection);
                        },
                        CancellationToken.None,
                        TaskContinuationOptions.ExecuteSynchronously,
                        TaskScheduler.Default);
                }
            }
        }).ConfigureAwait(false);

        if (snapshotTimeout.IsCancellationRequested)
        {
            logger.LogDebug("Dashboard URL snapshot lookup budget expired; unavailable URLs were omitted.");
        }
        for (var i = 0; i < hosts.Length; i++)
        {
            if (resolved[i])
            {
                _dashboardUrls[candidates[i].Connection] = hosts[i].DashboardUrl;
            }
        }
        return hosts;
    }

    private bool TryBeginDashboardLookup(IAppHostAuxiliaryBackchannel connection)
    {
        lock (_pendingDashboardLookups)
        {
            return _pendingDashboardLookups.Count < MaximumConcurrentDashboardLookups && _pendingDashboardLookups.Add(connection);
        }
    }

    private void CompleteDashboardLookup(IAppHostAuxiliaryBackchannel connection)
    {
        lock (_pendingDashboardLookups)
        {
            _pendingDashboardLookups.Remove(connection);
        }
    }

    private static Update Error(string code) => new(JsonSerializer.Serialize(new TrayWatchMessage
    {
        Version = TrayCliProtocol.Version,
        Type = "error",
        ErrorCode = code
    }, TrayCliJsonContext.Default.TrayWatchMessage), true);

    private sealed record Update(string Json, bool IsError);
}
