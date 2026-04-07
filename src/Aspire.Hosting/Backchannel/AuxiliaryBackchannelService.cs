// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net.Sockets;
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
    private Socket? _serverSocket;
    private readonly TaskCompletionSource _listeningTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

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
            // Create the socket path
            SocketPath = GetAuxiliaryBackchannelSocketPath(configuration);

            logger.LogDebug("Starting auxiliary backchannel service on socket path: {SocketPath}", SocketPath);

            // Ensure the directory exists
            var directory = Path.GetDirectoryName(SocketPath);
            if (directory != null && !Directory.Exists(directory))
            {
                logger.LogDebug("Creating backchannels directory: {Directory}", directory);
                Directory.CreateDirectory(directory);
            }

            // Clean up orphaned sockets from crashed instances of this same AppHost
            var appHostPath = configuration["AppHost:FilePath"] ?? configuration["AppHost:Path"];
            if (!string.IsNullOrEmpty(appHostPath))
            {
                var hash = BackchannelConstants.ComputeHash(appHostPath);
                var orphansDeleted = BackchannelConstants.CleanupOrphanedSockets(directory!, hash, Environment.ProcessId);
                if (orphansDeleted > 0)
                {
                    logger.LogDebug("Cleaned up {Count} orphaned socket(s) from previous instances.", orphansDeleted);
                }
            }

            // Clean up any existing socket file (shouldn't exist with PID in name, but just in case)
            if (File.Exists(SocketPath))
            {
                logger.LogDebug("Deleting existing socket file: {SocketPath}", SocketPath);
                File.Delete(SocketPath);
            }

            // Create and bind the server socket
            logger.LogDebug("Creating and binding server socket...");
            _serverSocket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            var endpoint = new UnixDomainSocketEndPoint(SocketPath);
            _serverSocket.Bind(endpoint);
            _serverSocket.Listen(backlog: 10); // Allow multiple pending connections

            logger.LogDebug("Auxiliary backchannel listening on {SocketPath}", SocketPath);
            _listeningTcs.TrySetResult();

            // Accept connections in a loop (supporting multiple concurrent connections)
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    var clientSocket = await _serverSocket.AcceptAsync(stoppingToken).ConfigureAwait(false);

                    // Handle each connection on a separate task
                    _ = Task.Run(async () => await HandleClientConnectionAsync(clientSocket, stoppingToken).ConfigureAwait(false), stoppingToken);
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
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error in auxiliary backchannel service.");
        }
        finally
        {
            // Clean up the socket
            _serverSocket?.Dispose();
            if (SocketPath != null && File.Exists(SocketPath))
            {
                try
                {
                    File.Delete(SocketPath);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Failed to delete socket file: {SocketPath}", SocketPath);
                }
            }
        }
    }

    private async Task HandleClientConnectionAsync(Socket clientSocket, CancellationToken stoppingToken)
    {
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
                serviceProvider);

            // Set up JSON-RPC over the client socket
            using var stream = new NetworkStream(clientSocket, ownsSocket: true);

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
            using var rpc = new JsonRpc(handler, rpcTarget);
            rpc.StartListening();

            // Wait for the connection to be disposed (client disconnect or cancellation)
            await rpc.Completion.ConfigureAwait(false);

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

    /// <summary>
    /// Generates the Unix socket path for the auxiliary backchannel.
    /// </summary>
    private static string GetAuxiliaryBackchannelSocketPath(IConfiguration configuration)
    {
        var homeDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        // Use AppHost:FilePath or AppHost:Path from configuration for consistent hashing
        // This matches the logic in AuxiliaryBackchannelRpcTarget.GetAppHostInformationAsync
        var appHostPath = configuration["AppHost:FilePath"] ?? configuration["AppHost:Path"];

        if (!string.IsNullOrEmpty(appHostPath))
        {
            // Use shared helper for consistent socket naming with PID
            return BackchannelConstants.ComputeSocketPath(appHostPath, homeDirectory, Environment.ProcessId);
        }

        // Fallback: Generate socket path using process ID as the "hash" (rare edge case)
        var backchannelsDir = BackchannelConstants.GetBackchannelsDirectory(homeDirectory);
        var fallbackHash = BackchannelConstants.ComputeHash(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return Path.Combine(backchannelsDir, $"{BackchannelConstants.SocketPrefix}.{fallbackHash}.{Environment.ProcessId}");
    }
}
