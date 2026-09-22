// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.IO.Pipes;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using Aspire.Hosting.RemoteHost.CodeGeneration;
using Aspire.Hosting.RemoteHost.Diagnostics;
using Aspire.Hosting.RemoteHost.Language;
using Aspire.Shared;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using StreamJsonRpc;

namespace Aspire.Hosting.RemoteHost;

internal sealed class JsonRpcServer : BackgroundService
{
    private readonly string _socketPath;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<JsonRpcServer> _logger;
    private readonly RemoteHostProfilingTelemetry _profilingTelemetry;
    private Socket? _listenSocket;
    private bool _disposed;
    private int _activeClientCount;

    public JsonRpcServer(
        IConfiguration configuration,
        IServiceScopeFactory scopeFactory,
        ILogger<JsonRpcServer> logger,
        RemoteHostProfilingTelemetry profilingTelemetry)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _profilingTelemetry = profilingTelemetry;

        var socketPath = configuration["REMOTE_APP_HOST_SOCKET_PATH"];
        if (string.IsNullOrEmpty(socketPath))
        {
            var tempDir = Path.GetTempPath();
            socketPath = Path.Combine(tempDir, "aspire", "remote-app-host.sock");
        }
        _socketPath = socketPath;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Starting RemoteAppHost JsonRpc Server on {SocketPath}...", _socketPath);
        var transport = OperatingSystem.IsWindows()
            ? RemoteHostProfilingTelemetry.Values.NamedPipe
            : RemoteHostProfilingTelemetry.Values.UnixDomainSocket;
        using var activity = _profilingTelemetry.StartJsonRpcListen(transport);

        try
        {
            if (OperatingSystem.IsWindows())
            {
                await StartNamedPipeServerAsync(activity, stoppingToken).ConfigureAwait(false);
            }
            else
            {
                await StartUnixSocketServerAsync(activity, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            activity.SetError(ex);
            throw;
        }

        _logger.LogInformation("Goodbye!");
    }

    [SupportedOSPlatform("windows")]
    private async Task StartNamedPipeServerAsync(RemoteHostProfilingTelemetry.ActivityScope listenActivity, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Starting JsonRpc server on named pipe: {SocketPath}", _socketPath);

        // Create pipe security that only allows the current user to connect
        // This is equivalent to the Unix socket permission (owner read/write only)
        var pipeSecurity = new PipeSecurity();
        using var identity = WindowsIdentity.GetCurrent();
        var currentUser = identity.User ?? throw new UnauthorizedAccessException("The current Windows user has no security identifier.");
        pipeSecurity.AddAccessRule(new PipeAccessRule(
            currentUser,
            PipeAccessRights.FullControl,
            AccessControlType.Allow));

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                _logger.LogDebug("Waiting for client connection...");

                // Create a new named pipe server for each connection with security restrictions
                var pipeServer = NamedPipeServerStreamAcl.Create(
                    _socketPath,
                    PipeDirection.InOut,
                    NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous,
                    inBufferSize: 0,
                    outBufferSize: 0,
                    pipeSecurity);

                listenActivity.AddJsonRpcServerListening();
                await pipeServer.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);

                _logger.LogDebug("Client connected");
                var activeClientCount = Interlocked.Increment(ref _activeClientCount);
                listenActivity.AddJsonRpcClientConnected(activeClientCount);

                // Handle the connection in a separate task - pipe stream is owned by handler
                _ = Task.Run(() => HandleClientStreamAsync(pipeServer, ownsStream: true, cancellationToken), cancellationToken);
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("Server shutdown requested");
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in server loop, retrying in 1 second...");
                await Task.Delay(1000, cancellationToken).ConfigureAwait(false);
            }
        }

        _logger.LogInformation("Server stopped");
    }

    private async Task StartUnixSocketServerAsync(RemoteHostProfilingTelemetry.ActivityScope listenActivity, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Starting JsonRpc server on Unix domain socket: {SocketPath}", _socketPath);

        SocketPermissionHelper.CreateDirectory(Path.GetDirectoryName(_socketPath)!);

        // Delete existing socket file if it exists
        if (File.Exists(_socketPath))
        {
            File.Delete(_socketPath);
        }

        _listenSocket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        SocketPermissionHelper.Bind(_listenSocket, _socketPath);

        _listenSocket.Listen(10);
        listenActivity.AddJsonRpcServerListening();

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                _logger.LogDebug("Waiting for client connection...");

                var clientSocket = await _listenSocket.AcceptAsync(cancellationToken).ConfigureAwait(false);

                _logger.LogDebug("Client connected");
                var activeClientCount = Interlocked.Increment(ref _activeClientCount);
                listenActivity.AddJsonRpcClientConnected(activeClientCount);

                // Handle the connection in a separate task - NetworkStream owns the socket
                var stream = new NetworkStream(clientSocket, ownsSocket: true);
                _ = Task.Run(() => HandleClientStreamAsync(stream, ownsStream: true, cancellationToken), cancellationToken);
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("Server shutdown requested");
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in server loop, retrying in 1 second...");
                await Task.Delay(1000, cancellationToken).ConfigureAwait(false);
            }
        }

        _logger.LogInformation("Server stopped");
    }

    private async Task HandleClientStreamAsync(Stream clientStream, bool ownsStream, CancellationToken cancellationToken)
    {
        var clientId = Guid.NewGuid().ToString("N")[..8]; // Short client identifier
        var disconnectReason = "unknown";
        using var activity = _profilingTelemetry.StartJsonRpcConnection();

        // Create a DI scope for this client connection
        // All scoped services (HandleRegistry, RemoteAppHostService, etc.) are per-client
        _logger.LogDebug("Creating DI scope for client {ClientId}", clientId);
        var scope = _scopeFactory.CreateAsyncScope();
        await using var _ = scope.ConfigureAwait(false);

        // Resolve the scoped RemoteAppHostService
        var clientService = scope.ServiceProvider.GetRequiredService<RemoteAppHostService>();
        var codeGenerationService = scope.ServiceProvider.GetRequiredService<CodeGenerationService>();
        var languageService = scope.ServiceProvider.GetRequiredService<LanguageService>();

        try
        {
            // Use System.Text.Json formatter instead of the default Newtonsoft.Json formatter
            var formatter = new SystemTextJsonFormatter();
            var handler = new HeaderDelimitedMessageHandler(clientStream, clientStream, formatter);
            using var jsonRpc = new JsonRpc(handler, clientService)
            {
                ActivityTracingStrategy = new ActivityTracingStrategy()
            };

            // Add the shared CodeGenerationService as an additional target for generateCode method
            jsonRpc.AddLocalRpcTarget(codeGenerationService);

            // Add the shared LanguageService as an additional target for language support methods
            jsonRpc.AddLocalRpcTarget(languageService);

            jsonRpc.StartListening();
            activity.AddJsonRpcListening();

            // Enable bidirectional communication - allow .NET to call back to TypeScript
            clientService.SetClientConnection(jsonRpc);

            _logger.LogDebug("JsonRpc connection established for client {ClientId} (bidirectional)", clientId);

            // Wait for the connection to be closed by the client, an error, or cancellation
            using var registration = cancellationToken.Register(() =>
            {
                disconnectReason = "server shutdown";
                try { jsonRpc.Dispose(); }
                catch { /* ignore disposal errors during cancellation */ }
            });

            try
            {
                await jsonRpc.Completion.ConfigureAwait(false);
                disconnectReason = "graceful disconnect";
                _logger.LogDebug("Client {ClientId}: {DisconnectReason}", clientId, disconnectReason);
            }
            catch (ConnectionLostException ex)
            {
                disconnectReason = "connection lost (client disconnected unexpectedly)";
                _logger.LogDebug(ex, "Client {ClientId}: {DisconnectReason}", clientId, disconnectReason);
            }
            catch (ObjectDisposedException)
            {
                // This happens when server shutdown causes jsonRpc.Dispose()
                disconnectReason ??= "server shutdown";
                _logger.LogDebug("Client {ClientId}: {DisconnectReason}", clientId, disconnectReason);
            }
            catch (IOException ex)
            {
                disconnectReason = "stream closed (client terminated)";
                _logger.LogDebug(ex, "Client {ClientId}: {DisconnectReason}", clientId, disconnectReason);
            }
        }
        catch (IOException ex)
        {
            activity.SetError(ex);
            _logger.LogWarning(ex, "Client {ClientId} I/O error", clientId);
        }
        catch (Exception ex)
        {
            activity.SetError(ex);
            _logger.LogError(ex, "Client {ClientId} unexpected error", clientId);
        }
        finally
        {
            // Clean up stream if we own it
            if (ownsStream)
            {
                try
                {
                    clientStream.Dispose();
                }
                catch
                {
                    // Ignore errors during close
                }
            }

            _logger.LogDebug("Connection cleanup completed for client {ClientId}", clientId);
            activity.AddJsonRpcConnectionClosed(disconnectReason);

            // Decrement active client count
            var remaining = Interlocked.Decrement(ref _activeClientCount);
            _logger.LogDebug("Active clients remaining: {RemainingClients}", remaining);
        }
    }

    public override void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;

            _listenSocket?.Dispose();

            // Only a filesystem listener that passed directory validation can own a socket file.
            if (_listenSocket is not null && File.Exists(_socketPath))
            {
                try
                {
                    File.Delete(_socketPath);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to delete socket file: {SocketPath}", _socketPath);
                }
            }

            _logger.LogDebug("JsonRpcServer disposed");
        }

        base.Dispose();
    }
}
