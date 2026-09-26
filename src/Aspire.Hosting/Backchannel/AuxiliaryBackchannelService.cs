// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net.Sockets;
using Aspire.Hosting.Diagnostics;
using Aspire.Hosting.Eventing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using StreamJsonRpc;

namespace Aspire.Hosting.Backchannel;

/// <summary>
/// Background service that listens for multiple concurrent connections on a Unix socket and provides MCP-related RPC operations.
/// </summary>
internal sealed class AuxiliaryBackchannelService(
    ILogger<AuxiliaryBackchannelService> logger,
    IConfiguration configuration,
    IDistributedApplicationEventing eventing,
    IServiceProvider serviceProvider)
    : BackgroundService
{
    private AppHostSocketManager.AppHostSocketListener? _appHostSocket;
    private readonly TaskCompletionSource _listeningTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object _clientTasksLock = new();
    private readonly HashSet<Task> _clientTasks = [];

    /// <summary>
    /// Gets the Unix socket path where the auxiliary backchannel is listening.
    /// </summary>
    public string? SocketPath { get; private set; }

    /// <summary>
    /// Gets a task that completes when the server socket is bound and listening for connections.
    /// </summary>
    /// <remarks>
    /// Used by tests to wait until the backchannel is ready before attempting to connect.
    /// </remarks>
    internal Task ListeningTask => _listeningTcs.Task;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            _appHostSocket = AppHostSocketManager.CreateSocket(
                GetAppHostPath(configuration),
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                Environment.ProcessId,
                logger);
            SocketPath = _appHostSocket.SocketPath;

            logger.LogDebug("Starting auxiliary backchannel service on socket path: {SocketPath}", SocketPath);

            logger.LogDebug("Auxiliary backchannel listening on {SocketPath}", SocketPath);
            _listeningTcs.TrySetResult();

            // Accept connections in a loop (supporting multiple concurrent connections)
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    var clientSocket = await _appHostSocket.Socket.AcceptAsync(stoppingToken).ConfigureAwait(false);

                    // Calling the async handler directly transfers socket ownership before the first await.
                    // Task.Run with the stopping token could cancel before invoking the delegate and leak an
                    // already-accepted socket.
                    var clientTask = HandleClientConnectionAsync(clientSocket, stoppingToken);
                    lock (_clientTasksLock)
                    {
                        _clientTasks.Add(clientTask);
                    }
                    _ = ObserveClientTaskAsync(clientTask);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    // Expected when shutting down
                    break;
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Error accepting client connection on auxiliary backchannel.");
                }
            }
        }
        catch (TaskCanceledException ex)
        {
            logger.LogDebug("Auxiliary backchannel service was cancelled: {Message}", ex.Message);
            _listeningTcs.TrySetCanceled(stoppingToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error in auxiliary backchannel service.");
            _listeningTcs.TrySetException(ex);
        }
        finally
        {
            // Creating the socket can fail (bind failure, an AF_UNIX path over the platform byte
            // limit, permissions) and the accept loop only completes the source once it is already
            // listening, so guarantee completion on every exit path. Waiters would otherwise block
            // until their own timeout instead of observing the failure.
            _listeningTcs.TrySetCanceled(stoppingToken);

            // Nothing outside tests awaits ListeningTask, so read the fault here to mark it observed
            // and keep it from resurfacing as an UnobservedTaskException when the task is finalized.
            _ = _listeningTcs.Task.Exception;

            _appHostSocket?.Dispose();

            Task[] clientTasks;
            lock (_clientTasksLock)
            {
                clientTasks = [.. _clientTasks];
            }
            await Task.WhenAll(clientTasks).ConfigureAwait(false);
        }
    }

    private async Task HandleClientConnectionAsync(Socket clientSocket, CancellationToken stoppingToken)
    {
        using var ownedClientSocket = clientSocket;

        try
        {
            logger.LogDebug("Client connected to auxiliary backchannel.");

            // Publish the connected event
            var connectedEvent = new AuxiliaryBackchannelConnectedEvent(serviceProvider, SocketPath!, clientSocket);
            await eventing.PublishAsync(
                connectedEvent,
                EventDispatchBehavior.NonBlockingConcurrent,
                stoppingToken).ConfigureAwait(false);

            // Create a new RPC target for this connection
            var rpcTarget = new AuxiliaryBackchannelRpcTarget(
                serviceProvider.GetRequiredService<ILogger<AuxiliaryBackchannelRpcTarget>>(),
                serviceProvider.GetRequiredService<IConfiguration>(),
                serviceProvider.GetRequiredService<ProfilingTelemetry>(),
                serviceProvider);

            // Set up JSON-RPC over the client socket
            using var stream = new NetworkStream(ownedClientSocket, ownsSocket: false);

            // Create JSON-RPC connection with proper System.Text.Json formatter so it doesn't use Newtonsoft.Json
            // and handles correct MCP SDK type serialization
            // Configure to use camelCase naming to match CLI's MCP SDK options
            var formatter = new SystemTextJsonFormatter();
            formatter.JsonSerializerOptions = new System.Text.Json.JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase
            };

            var handler = new HeaderDelimitedMessageHandler(stream, formatter);
            using var rpc = new JsonRpc(handler, rpcTarget)
            {
                ActivityTracingStrategy = new ActivityTracingStrategy()
            };
            rpc.StartListening();

            // Stop waiting when the hosted service is shutting down so the RPC, stream, and socket
            // are disposed before the application's service provider is torn down.
            await rpc.Completion.WaitAsync(stoppingToken).ConfigureAwait(false);

            logger.LogDebug("Client disconnected from auxiliary backchannel");
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            logger.LogDebug("Client connection handler was cancelled");
        }
        catch (IOException ex) when (ex.InnerException is SocketException { SocketErrorCode: SocketError.ConnectionReset })
        {
            // IOException wrapping a ConnectionReset SocketException is expected when the client
            // disconnects abruptly (e.g., process exit). This is a normal condition and not an error.
            logger.LogDebug(ex, "Client disconnected from auxiliary backchannel");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error handling client connection on auxiliary backchannel");
        }
    }

    private async Task ObserveClientTaskAsync(Task clientTask)
    {
        try
        {
            await clientTask.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // HandleClientConnectionAsync handles connection failures itself. Reaching this catch means
            // its cleanup path failed, and this detached observer is the only place that can report it.
            logger.LogError(ex, "Unexpected error while observing an auxiliary backchannel client");
        }
        finally
        {
            lock (_clientTasksLock)
            {
                _clientTasks.Remove(clientTask);
            }
        }
    }

    private static string? GetAppHostPath(IConfiguration configuration) =>
        configuration["AppHost:FilePath"] ?? configuration["AppHost:Path"];
}
