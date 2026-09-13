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
        Task producer = Task.CompletedTask;
        Task<bool>? pendingRead = null;
        Task<bool>? pendingHeartbeat = null;
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
                if (!await connections.MoveNextAsync().ConfigureAwait(false))
                {
                    return Error("discovery_failed");
                }

                var hosts = new List<TrayAppHost>();
                foreach (var connection in connections.Current)
                {
                    if (connection.AppHostInfo is not { } info)
                    {
                        continue;
                    }
                    if (hosts.Count == TrayCliProtocol.MaximumAppHosts)
                    {
                        return Error("limit_exceeded");
                    }

                    string? dashboardUrl = null;
                    try
                    {
                        dashboardUrl = (await connection.GetDashboardUrlsAsync(token).ConfigureAwait(false))?.BaseUrlWithLoginToken;
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        logger.LogDebug(ex, "Dashboard URL unavailable for AppHost PID {Pid}.", info.ProcessId);
                    }

                    var startedAt = processIdentityProvider.GetStartTimeUnixMilliseconds(info.ProcessId);
                    hosts.Add(new TrayAppHost
                    {
                        AppHostPath = info.AppHostPath,
                        AppHostPid = info.ProcessId,
                        ProcessStartTimeUnixMilliseconds = startedAt is > 0 ? startedAt : null,
                        DashboardUrl = dashboardUrl
                    });
                }

                var message = new TrayWatchMessage
                {
                    Version = TrayCliProtocol.Version,
                    Type = "snapshot",
                    AppHosts = hosts.OrderBy(host => host.AppHostPath, StringComparer.Ordinal)
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

    private static Update Error(string code) => new(JsonSerializer.Serialize(new TrayWatchMessage
    {
        Version = TrayCliProtocol.Version,
        Type = "error",
        ErrorCode = code
    }, TrayCliJsonContext.Default.TrayWatchMessage), true);

    private sealed record Update(string Json, bool IsError);
}
