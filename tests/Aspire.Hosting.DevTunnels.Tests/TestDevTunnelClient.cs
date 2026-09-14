// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Concurrent;
using System.Globalization;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.DevTunnels.Tests;

internal sealed class TestDevTunnelClient(Version? cliVersion = null) : IDevTunnelClient
{
    private readonly Version _cliVersion = cliVersion ?? DevTunnelCli.MinimumSupportedVersion;

    public ConcurrentQueue<DevTunnelClientCall> Calls { get; } = new();

    public UserLoginStatus LoginStatus { get; set; } = new("Logged in", LoginProvider.Microsoft, "test-user");

    public DevTunnelPortList PortList { get; set; } = new();

    public DevTunnelStatus TunnelStatus { get; set; } = new("test-tunnel", HostConnections: 1, ClientConnections: 0, Description: "", Labels: []);

    public DevTunnelAccessStatus AccessStatus { get; set; } = new();

    public Task<Version> GetVersionAsync(ILogger? logger = null, CancellationToken cancellationToken = default)
    {
        Calls.Enqueue(new(nameof(GetVersionAsync)));
        return Task.FromResult(_cliVersion);
    }

    public Task<UserLoginStatus> GetUserLoginStatusAsync(ILogger? logger = null, CancellationToken cancellationToken = default)
    {
        Calls.Enqueue(new(nameof(GetUserLoginStatusAsync)));
        return Task.FromResult(LoginStatus);
    }

    public Task<UserLoginStatus> UserLoginAsync(LoginProvider provider, ILogger? logger = null, CancellationToken cancellationToken = default)
    {
        Calls.Enqueue(new(nameof(UserLoginAsync)));
        return Task.FromResult(LoginStatus with { Provider = provider });
    }

    public Task<DevTunnelStatus> CreateTunnelAsync(string tunnelId, DevTunnelOptions options, ILogger? logger = null, CancellationToken cancellationToken = default)
    {
        Calls.Enqueue(new(nameof(CreateTunnelAsync), tunnelId));
        return Task.FromResult(new DevTunnelStatus(tunnelId, HostConnections: 1, ClientConnections: 0, Description: "", Labels: []));
    }

    public Task<DevTunnelPortList> GetPortListAsync(string tunnelId, ILogger? logger = null, CancellationToken cancellationToken = default)
    {
        Calls.Enqueue(new(nameof(GetPortListAsync), tunnelId));
        return Task.FromResult(PortList);
    }

    public Task<DevTunnelPortStatus> CreatePortAsync(string tunnelId, int portNumber, DevTunnelPortOptions options, ILogger? logger = null, CancellationToken cancellationToken = default)
    {
        Calls.Enqueue(new(nameof(CreatePortAsync), tunnelId, portNumber));
        return Task.FromResult(new DevTunnelPortStatus(tunnelId, portNumber, options.Protocol ?? "https", ClientConnections: 0));
    }

    public Task<DevTunnelPortDeleteResult> DeletePortAsync(string tunnelId, int portNumber, ILogger? logger = null, CancellationToken cancellationToken = default)
    {
        Calls.Enqueue(new(nameof(DeletePortAsync), tunnelId, portNumber));
        return Task.FromResult(new DevTunnelPortDeleteResult(portNumber.ToString(CultureInfo.InvariantCulture)));
    }

    public Task<DevTunnelStatus> GetTunnelAsync(string tunnelId, ILogger? logger = null, CancellationToken cancellationToken = default)
    {
        Calls.Enqueue(new(nameof(GetTunnelAsync), tunnelId));
        return Task.FromResult(TunnelStatus);
    }

    public Task<DevTunnelAccessStatus> GetAccessAsync(string tunnelId, int? portNumber = null, ILogger? logger = null, CancellationToken cancellationToken = default)
    {
        Calls.Enqueue(new(nameof(GetAccessAsync), tunnelId, portNumber));
        return Task.FromResult(AccessStatus);
    }
}

internal sealed record DevTunnelClientCall(string Method, string? TunnelId = null, int? PortNumber = null);
