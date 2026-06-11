// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.Eventing;

namespace Aspire.Hosting.Backchannel;

/// <summary>
/// Event that is published when a client connects to the auxiliary backchannel.
/// </summary>
internal sealed class AuxiliaryBackchannelConnectedEvent(IServiceProvider serviceProvider, string socketPath, System.Net.Sockets.Socket clientSocket) : IDistributedApplicationEvent
{
    /// <summary>
    /// Gets the service provider for the application.
    /// </summary>
    public IServiceProvider Services { get; } = serviceProvider;

    /// <summary>
    /// Gets the Unix socket path where the auxiliary backchannel is listening.
    /// </summary>
    public string SocketPath { get; } = socketPath;

    /// <summary>
    /// Gets the client socket that connected to the auxiliary backchannel.
    /// </summary>
    public System.Net.Sockets.Socket ClientSocket { get; } = clientSocket;
}
