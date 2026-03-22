// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using Aspire.Cli.Resources;
using Aspire.Cli.Telemetry;
using Microsoft.Extensions.Logging;
using StreamJsonRpc;

namespace Aspire.Cli.Backchannel;

internal interface IAppHostCliBackchannel
{
    Task RequestStopAsync(CancellationToken cancellationToken);
    Task<DashboardUrlsState> GetDashboardUrlsAsync(CancellationToken cancellationToken);
    IAsyncEnumerable<BackchannelLogEntry> GetAppHostLogEntriesAsync(CancellationToken cancellationToken);
    IAsyncEnumerable<RpcResourceState> GetResourceStatesAsync(CancellationToken cancellationToken);
    Task ConnectAsync(string socketPath, CancellationToken cancellationToken);
    Task ConnectAsync(string socketPath, bool autoReconnect, CancellationToken cancellationToken);
    IAsyncEnumerable<PublishingActivity> GetPublishingActivitiesAsync(CancellationToken cancellationToken);
    Task<string[]> GetCapabilitiesAsync(CancellationToken cancellationToken);
    Task CompletePromptResponseAsync(string promptId, PublishingPromptInputAnswer[] answers, CancellationToken cancellationToken);
    Task UpdatePromptResponseAsync(string promptId, PublishingPromptInputAnswer[] answers, CancellationToken cancellationToken);
    IAsyncEnumerable<CommandOutput> ExecAsync(CancellationToken cancellationToken);
}

internal sealed class AppHostCliBackchannel(ILogger<AppHostCliBackchannel> logger, AspireCliTelemetry telemetry) : IAppHostCliBackchannel
{
    private const string BaselineCapability = "baseline.v2";
    private TaskCompletionSource<JsonRpc> _rpcTaskCompletionSource = new();
    private string? _socketPath;
    private bool _autoReconnect;
    private CancellationToken _cancellationToken;
    private readonly object _lock = new();
    private volatile bool _isReconnecting;

    /// <summary>
    /// Gets the current RPC task in a thread-safe manner.
    /// </summary>
    private Task<JsonRpc> GetRpcTaskAsync()
    {
        lock (_lock)
        {
            return _rpcTaskCompletionSource.Task;
        }
    }

    public async Task RequestStopAsync(CancellationToken cancellationToken)
    {
        // This RPC call is required to allow the CLI to trigger a clean shutdown
        // of the AppHost process. The AppHost process will then trigger the shutdown
        // which will allow the CLI to await the pending run.

        using var activity = telemetry.StartDiagnosticActivity();
        var rpc = await GetRpcTaskAsync().WaitAsync(cancellationToken).ConfigureAwait(false);

        logger.LogDebug("Requesting stop");

        await rpc.InvokeWithCancellationAsync(
            "RequestStopAsync",
            [],
            cancellationToken);
    }

    public async Task<DashboardUrlsState> GetDashboardUrlsAsync(CancellationToken cancellationToken)
    {
        using var activity = telemetry.StartDiagnosticActivity();
        var rpc = await GetRpcTaskAsync().WaitAsync(cancellationToken).ConfigureAwait(false);

        logger.LogDebug("Requesting dashboard URL");

        var state = await rpc.InvokeWithCancellationAsync<DashboardUrlsState>(
            "GetDashboardUrlsAsync",
            [],
            cancellationToken);
        return state;
    }

    public async IAsyncEnumerable<BackchannelLogEntry> GetAppHostLogEntriesAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            IAsyncEnumerable<BackchannelLogEntry>? logEntries = null;
            try
            {
                using var activity = telemetry.StartDiagnosticActivity();
                var rpc = await GetRpcTaskAsync().WaitAsync(cancellationToken).ConfigureAwait(false);

                logger.LogDebug("Requesting AppHost log entries");

                logEntries = await rpc.InvokeWithCancellationAsync<IAsyncEnumerable<BackchannelLogEntry>>(
                    "GetAppHostLogEntriesAsync",
                    [],
                    cancellationToken);

                logger.LogDebug("Received AppHost log entries async enumerable");
            }
            catch (Exception ex) when (_autoReconnect && !cancellationToken.IsCancellationRequested && IsConnectionLostException(ex))
            {
                logger.LogDebug("Connection lost while getting log entries, waiting for reconnect...");
                await WaitForReconnectionAsync(cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (logEntries is not null)
            {
                await foreach (var entry in EnumerateWithReconnect(logEntries, cancellationToken))
                {
                    yield return entry;
                }
            }
        }
    }

    public async IAsyncEnumerable<RpcResourceState> GetResourceStatesAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            IAsyncEnumerable<RpcResourceState>? resourceStates = null;
            try
            {
                using var activity = telemetry.StartDiagnosticActivity();
                var rpc = await GetRpcTaskAsync().WaitAsync(cancellationToken).ConfigureAwait(false);

                logger.LogDebug("Requesting resource states");

                resourceStates = await rpc.InvokeWithCancellationAsync<IAsyncEnumerable<RpcResourceState>>(
                    "GetResourceStatesAsync",
                    [],
                    cancellationToken);

                logger.LogDebug("Received resource states async enumerable");
            }
            catch (Exception ex) when (_autoReconnect && !cancellationToken.IsCancellationRequested && IsConnectionLostException(ex))
            {
                logger.LogDebug("Connection lost while getting resource states, waiting for reconnect...");
                await WaitForReconnectionAsync(cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (resourceStates is not null)
            {
                await foreach (var state in EnumerateWithReconnect(resourceStates, cancellationToken))
                {
                    yield return state;
                }
            }
        }
    }

    private async IAsyncEnumerable<T> EnumerateWithReconnect<T>(IAsyncEnumerable<T> source, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var enumerator = source.GetAsyncEnumerator(cancellationToken);
        try
        {
            while (true)
            {
                bool hasNext;
                T current;
                try
                {
                    hasNext = await enumerator.MoveNextAsync().ConfigureAwait(false);
                    if (!hasNext)
                    {
                        yield break;
                    }
                    current = enumerator.Current;
                }
                catch (Exception ex) when (_autoReconnect && !cancellationToken.IsCancellationRequested && IsConnectionLostException(ex))
                {
                    logger.LogDebug("Connection lost during enumeration, will restart after reconnect");
                    yield break; // Exit this enumeration, outer loop will restart
                }

                yield return current;
            }
        }
        finally
        {
            // Disposing a dead connection's enumerator may throw - suppress it
            try
            {
                await enumerator.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (IsConnectionLostException(ex))
            {
                logger.LogDebug("Ignoring connection lost exception during enumerator disposal");
            }
        }
    }

    private static bool IsConnectionLostException(Exception ex)
    {
        return ex is ConnectionLostException
            || ex is ObjectDisposedException
            || (ex is OperationCanceledException && ex.InnerException is ConnectionLostException);
    }

    private async Task WaitForReconnectionAsync(CancellationToken cancellationToken)
    {
        // Wait for the TCS to be reset and then completed again
        var startTime = DateTime.UtcNow;
        var maxWait = TimeSpan.FromSeconds(60);

        // First, wait for the reconnection to start (TCS to be reset)
        // This handles the race where we catch the exception before OnDisconnected fires
        Task<JsonRpc>? initialTask = null;
        while (!cancellationToken.IsCancellationRequested && DateTime.UtcNow - startTime < maxWait)
        {
            var currentTask = GetRpcTaskAsync();

            // If this is a new TCS (different from what we had), reconnection has started
            if (initialTask is not null && !ReferenceEquals(currentTask, initialTask))
            {
                break;
            }

            // If we haven't captured the initial task yet, do so
            initialTask ??= currentTask;

            // If the current task is not completed, reconnection has started (TCS was reset)
            if (!currentTask.IsCompleted)
            {
                break;
            }

            await Task.Delay(100, cancellationToken).ConfigureAwait(false);
        }

        // Now wait for the reconnection to complete
        while (!cancellationToken.IsCancellationRequested && DateTime.UtcNow - startTime < maxWait)
        {
            var rpcTask = GetRpcTaskAsync();
            if (rpcTask.IsCompletedSuccessfully)
            {
                logger.LogDebug("Reconnection completed successfully");
                return;
            }

            await Task.Delay(500, cancellationToken).ConfigureAwait(false);
        }

        logger.LogWarning("Timed out waiting for backchannel reconnection");
    }

    public Task ConnectAsync(string socketPath, CancellationToken cancellationToken)
        => ConnectAsync(socketPath, autoReconnect: false, cancellationToken);

    public async Task ConnectAsync(string socketPath, bool autoReconnect, CancellationToken cancellationToken)
    {
        try
        {
            using var activity = telemetry.StartDiagnosticActivity();

            lock (_lock)
            {
                if (_rpcTaskCompletionSource.Task.IsCompleted && !_rpcTaskCompletionSource.Task.IsFaulted)
                {
                    throw new InvalidOperationException(ErrorStrings.AlreadyConnectedToBackchannel);
                }
            }

            _socketPath = socketPath;
            _autoReconnect = autoReconnect;
            _cancellationToken = cancellationToken;

            logger.LogDebug("Connecting to AppHost backchannel at {SocketPath} (autoReconnect={AutoReconnect})", socketPath, autoReconnect);
            var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            var endpoint = new UnixDomainSocketEndPoint(socketPath);
            await socket.ConnectAsync(endpoint, cancellationToken);
            logger.LogDebug("Connected to AppHost backchannel at {SocketPath}", socketPath);

            var stream = new NetworkStream(socket, true);
            var rpc = new JsonRpc(new HeaderDelimitedMessageHandler(stream, stream, BackchannelJsonSerializerContext.CreateRpcMessageFormatter()));
            rpc.StartListening();

            var capabilities = await rpc.InvokeWithCancellationAsync<string[]>(
                "GetCapabilitiesAsync",
                [],
                cancellationToken);

            if (!capabilities.Any(s => s == BaselineCapability))
            {
                throw new AppHostIncompatibleException(
                    string.Format(CultureInfo.CurrentCulture, ErrorStrings.AppHostIncompatibleWithCli, BaselineCapability),
                    BaselineCapability
                    );
            }

            // Set up auto-reconnect if enabled
            if (autoReconnect)
            {
                rpc.Disconnected += OnDisconnected;
            }

            lock (_lock)
            {
                _rpcTaskCompletionSource.SetResult(rpc);
            }
        }
        catch (RemoteMethodNotFoundException ex)
        {
            logger.LogError(ex, "Failed to connect to AppHost backchannel. The AppHost must be updated to a version that supports the {BaselineCapability} capability.", BaselineCapability);
            throw new AppHostIncompatibleException(
                string.Format(CultureInfo.CurrentCulture, ErrorStrings.AppHostIncompatibleWithCli, BaselineCapability),
                BaselineCapability
                );
        }
    }

    private void OnDisconnected(object? sender, JsonRpcDisconnectedEventArgs args)
    {
        // Prevent concurrent reconnection attempts
        lock (_lock)
        {
            if (_isReconnecting)
            {
                logger.LogDebug("Backchannel disconnected but reconnection already in progress, ignoring.");
                return;
            }
            _isReconnecting = true;
        }

        logger.LogInformation("Backchannel disconnected: {Reason}. Attempting to reconnect...", args.Reason);
        _ = Task.Run(async () =>
        {
            try
            {
                await ReconnectInternalAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to reconnect backchannel");
            }
            finally
            {
                lock (_lock)
                {
                    _isReconnecting = false;
                }
            }
        });
    }

    private void ResetForReconnection()
    {
        lock (_lock)
        {
            logger.LogDebug("Resetting backchannel for reconnection");
            _rpcTaskCompletionSource = new TaskCompletionSource<JsonRpc>();
        }
    }

    private async Task ReconnectInternalAsync()
    {
        if (_socketPath is null)
        {
            throw new InvalidOperationException("Cannot reconnect: no previous connection.");
        }

        ResetForReconnection();

        // Wait for the new socket to appear (the new DistributedApplication needs to start)
        var startTime = DateTime.UtcNow;
        var maxWait = TimeSpan.FromSeconds(30);

        while (!_cancellationToken.IsCancellationRequested)
        {
            try
            {
                await ConnectAsync(_socketPath, _autoReconnect, _cancellationToken).ConfigureAwait(false);
                logger.LogInformation("Successfully reconnected to backchannel");
                return;
            }
            catch (SocketException) when (DateTime.UtcNow - startTime < maxWait)
            {
                // Socket not ready yet, wait and retry
                await Task.Delay(500, _cancellationToken).ConfigureAwait(false);
            }
        }

        logger.LogWarning("Timed out waiting for backchannel reconnection");
    }

    public async IAsyncEnumerable<PublishingActivity> GetPublishingActivitiesAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var activity = telemetry.StartDiagnosticActivity();
        var rpc = await GetRpcTaskAsync().WaitAsync(cancellationToken).ConfigureAwait(false);

        logger.LogDebug("Requesting publishing activities.");

        var publishingActivities = await rpc.InvokeWithCancellationAsync<IAsyncEnumerable<PublishingActivity>>(
            "GetPublishingActivitiesAsync",
            [],
            cancellationToken);

        logger.LogDebug("Received publishing activities.");

        await foreach (var state in publishingActivities.WithCancellation(cancellationToken))
        {
            yield return state;
        }
    }

    public async Task<string[]> GetCapabilitiesAsync(CancellationToken cancellationToken)
    {
        using var activity = telemetry.StartDiagnosticActivity();
        var rpc = await GetRpcTaskAsync().WaitAsync(cancellationToken).ConfigureAwait(false);

        logger.LogDebug("Requesting capabilities");

        var capabilities = await rpc.InvokeWithCancellationAsync<string[]>(
            "GetCapabilitiesAsync",
            [],
            cancellationToken).ConfigureAwait(false);

        return capabilities;
    }

    public async Task CompletePromptResponseAsync(string promptId, PublishingPromptInputAnswer[] answers, CancellationToken cancellationToken)
    {
        using var activity = telemetry.StartDiagnosticActivity();
        var rpc = await GetRpcTaskAsync().WaitAsync(cancellationToken).ConfigureAwait(false);

        logger.LogDebug("Providing prompt responses for prompt ID {PromptId}", promptId);

        await rpc.InvokeWithCancellationAsync(
            "CompletePromptResponseAsync",
            [promptId, answers],
            cancellationToken).ConfigureAwait(false);
    }

    public async Task UpdatePromptResponseAsync(string promptId, PublishingPromptInputAnswer[] answers, CancellationToken cancellationToken)
    {
        using var activity = telemetry.StartDiagnosticActivity();
        var rpc = await GetRpcTaskAsync().WaitAsync(cancellationToken).ConfigureAwait(false);

        logger.LogDebug("Providing prompt responses for prompt ID {PromptId}", promptId);

        await rpc.InvokeWithCancellationAsync(
            "UpdatePromptResponseAsync",
            [promptId, answers],
            cancellationToken).ConfigureAwait(false);
    }

    public async IAsyncEnumerable<CommandOutput> ExecAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var activity = telemetry.StartDiagnosticActivity();
        var rpc = await GetRpcTaskAsync().WaitAsync(cancellationToken).ConfigureAwait(false);

        logger.LogDebug("Requesting execution.");
        var commandOutputs = await rpc.InvokeWithCancellationAsync<IAsyncEnumerable<CommandOutput>>(
            "ExecAsync",
            Array.Empty<object>(),
            cancellationToken);

        logger.LogDebug("Requested execution.");
        await foreach (var commandOutput in commandOutputs.WithCancellation(cancellationToken))
        {
            yield return commandOutput;
        }
    }

}

