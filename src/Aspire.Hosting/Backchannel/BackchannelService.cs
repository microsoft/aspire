// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net.Sockets;
using Aspire.Hosting.Backchannel;
using Aspire.Hosting.Eventing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using StreamJsonRpc;

namespace Aspire.Hosting.Cli;

internal sealed class BackchannelService(
    ILogger<BackchannelService> logger,
    IConfiguration configuration,
    AppHostRpcTarget appHostRpcTarget,
    IDistributedApplicationEventing eventing,
    IServiceProvider serviceProvider)
    : BackgroundService
{
    private JsonRpc? _rpc;
    private Socket? _serverSocket;
    private string? _socketPath;

    public bool IsBackchannelExpected => configuration.GetValue<string>(KnownConfigNames.UnixSocketPath) is {};

    private readonly TaskCompletionSource _backchannelConnectedTcs = new();

    public Task BackchannelConnected => _backchannelConnectedTcs.Task;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            var unixSocketPath = configuration.GetValue<string>(KnownConfigNames.UnixSocketPath);

            if (string.IsNullOrEmpty(unixSocketPath))
            {
                logger.LogDebug("Backchannel socket path was not specified.");
                return;
            }

            _socketPath = unixSocketPath;

            // Delete existing socket file if it exists (stale from previous run)
            if (File.Exists(unixSocketPath))
            {
                logger.LogDebug("Deleting existing socket file: {SocketPath}", unixSocketPath);
                File.Delete(unixSocketPath);
            }

            logger.LogDebug("Listening for backchannel connection on socket path: {SocketPath}", unixSocketPath);
            var serverSocket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            _serverSocket = serverSocket;
            var endpoint = new UnixDomainSocketEndPoint(unixSocketPath);
            serverSocket.Bind(endpoint);
            serverSocket.Listen();

            var backchannelReadyEvent = new BackchannelReadyEvent(serviceProvider, unixSocketPath);
            await eventing.PublishAsync(
                backchannelReadyEvent,
                EventDispatchBehavior.NonBlockingConcurrent,
                stoppingToken).ConfigureAwait(false);

            var clientSocket = await serverSocket.AcceptAsync(stoppingToken).ConfigureAwait(false);
            var stream = new NetworkStream(clientSocket, true);
            var rpc = JsonRpc.Attach(stream, appHostRpcTarget);
            _rpc = rpc;

            // NOTE: The PipelineExecutor will await this TCS
            //       when a backchannel is expected, and will not stop
            //       the application itself - it will instead wait for
            //       the CLI to stop the application explicitly.
            _backchannelConnectedTcs.SetResult();

            var backchannelConnectedEvent = new BackchannelConnectedEvent(serviceProvider, unixSocketPath);
            await eventing.PublishAsync(
                backchannelConnectedEvent,
                EventDispatchBehavior.NonBlockingConcurrent,
                stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex)
        {
            // This exception is expected when the service is shut down whilst waiting for
            // a socket and just means that we don't need to wait anymore.
            logger.LogDebug("Backchannel service was cancelled: {Message}", ex.Message);
            return;
        }
    }

    public override void Dispose()
    {
        // Dispose the RPC connection
        _rpc?.Dispose();
        _rpc = null;

        // Close and dispose the server socket
        if (_serverSocket is not null)
        {
            try
            {
                _serverSocket.Close();
                _serverSocket.Dispose();
            }
            catch
            {
                // Ignore errors during socket cleanup
            }
            _serverSocket = null;
        }

        // Delete the socket file to allow rebinding
        if (_socketPath is not null && File.Exists(_socketPath))
        {
            try
            {
                File.Delete(_socketPath);
                logger.LogDebug("Deleted backchannel socket file: {SocketPath}", _socketPath);
            }
            catch
            {
                // Ignore errors during file cleanup
            }
        }

        base.Dispose();
    }
}
