// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.Extensions.Configuration;

namespace Aspire.Hosting.DevTunnels.Tests;

public class DevTunnelCliClientTests
{
    [Fact]
    public async Task CreateTunnelAsync_WhenCreateConflictsAndUpdateIsNotFound_FailsWithoutRetrying()
    {
        var cli = new TestDevTunnelCli();
        cli.EnqueueCreateResult(
            DevTunnelCli.ResourceConflictsWithExistingExitCode,
            error: "Tunnel service error: Conflict with existing entity. Retry tunnel operation.");
        cli.EnqueueUpdateResult(
            DevTunnelCli.ResourceNotFoundExitCode,
            error: "Tunnel not found: ghost.eun1");

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ASPIRE_DEVTUNNEL_CLI_MAX_ATTEMPTS"] = "3"
            })
            .Build();
        var client = new DevTunnelCliClient(configuration, cli);
        var options = new DevTunnelOptions
        {
            Region = DevTunnelRegion.NorthEurope
        };

        var exception = await Assert.ThrowsAsync<DistributedApplicationException>(
            () => client.CreateTunnelAsync("ghost", options));

        Assert.Equal(
            """
            Dev tunnel 'ghost.eun1' could not be created because the dev tunnels service reported that it already exists, but then reported it was not found when Aspire tried to update it. This tunnel ID is in an inconsistent service state and retrying it cannot recover. Specify a different tunnel ID with AddDevTunnel(name, tunnelId: "new-id") and restart the AppHost. Create error: 'Tunnel service error: Conflict with existing entity. Retry tunnel operation.'. Update error: 'Tunnel not found: ghost.eun1'.
            """,
            exception.Message);
        Assert.Collection(
            cli.Calls,
            call =>
            {
                Assert.Equal(nameof(DevTunnelCli.CreateTunnelAsync), call.Method);
                Assert.Equal("ghost", call.TunnelId);
            },
            call =>
            {
                Assert.Equal(nameof(DevTunnelCli.UpdateTunnelAsync), call.Method);
                Assert.Equal("ghost.eun1", call.TunnelId);
            });
    }
}
