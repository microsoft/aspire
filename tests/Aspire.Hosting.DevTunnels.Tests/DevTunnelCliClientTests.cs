// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using Microsoft.Extensions.Configuration;

namespace Aspire.Hosting.DevTunnels.Tests;

public class DevTunnelCliClientTests
{
    [Fact]
    public async Task CreatePortIncludesModeledDescription()
    {
        var cli = new TestDevTunnelCli();
        cli.EnqueueCreatePortResult(0);
        await cli.CreatePortAsync("mytunnel.usw2", 3000, new()
        {
            Protocol = "https",
            Description = "target/https",
            Labels = ["target", "https"]
        });
        await Verify(Assert.Single(cli.Calls).Arguments);
    }

    [Theory]
    [InlineData(null, "30 days")]
    [InlineData(720, "30 days")]
    [InlineData(24, "24 hours")]
    [InlineData(25, "1 days, 1 hours")]
    public async Task ExistingTunnelWithMatchingConfigurationDoesNotWrite(int? expirationHours, string expiration)
    {
        var cli = new TestDevTunnelCli();
        cli.EnqueueShowResult(0, TunnelJson(expiration: expiration));
        var result = await CreateClient(cli).CreateTunnelAsync("mytunnel", new()
        {
            Description = "expected",
            Labels = ["label"],
            ExpirationHours = expirationHours
        });
        Assert.Equal("mytunnel.usw2", result.TunnelId);
        Assert.Equal(nameof(DevTunnelCli.ShowTunnelAsync), Assert.Single(cli.Calls).Method);
    }

    [Fact]
    public async Task EmptyDescriptionsDoNotRewriteRemoteMetadata()
    {
        var cli = new TestDevTunnelCli();
        cli.EnqueueShowResult(0, TunnelJson());
        cli.EnqueueShowPortResult(0, PortJson());
        var client = CreateClient(cli);
        await client.CreateTunnelAsync("mytunnel", new() { Description = "" });
        await client.CreatePortAsync("mytunnel.usw2", 3000, new() { Description = "", Protocol = "http", Labels = ["label"] });
        Assert.Equal([nameof(DevTunnelCli.ShowTunnelAsync), nameof(DevTunnelCli.ShowPortAsync)], cli.Calls.Select(c => c.Method));
    }

    [Fact]
    public async Task MetadataDriftUsesQualifiedIdentityAndUnwrapsUpdateResponse()
    {
        var cli = new TestDevTunnelCli();
        cli.EnqueueShowResult(0, TunnelJson(description: "old"));
        cli.EnqueueUpdateResult(0, TunnelJson(description: "new"));
        var result = await CreateClient(cli).CreateTunnelAsync("mytunnel", new() { Description = "new" });
        Assert.Equal("mytunnel.usw2", result.TunnelId);
        Assert.Collection(cli.Calls,
            call => Assert.Equal(nameof(DevTunnelCli.ShowTunnelAsync), call.Method),
            call =>
            {
                Assert.Equal(nameof(DevTunnelCli.UpdateTunnelAsync), call.Method);
                Assert.Equal("mytunnel.usw2", call.TunnelId);
            });
    }

    [Fact]
    public async Task MissingTunnelStatusHasAnAuthoritativeFailureType()
    {
        var cli = new TestDevTunnelCli();
        cli.EnqueueShowResult(DevTunnelCli.ResourceNotFoundExitCode, error: "Tunnel not found.");
        await Assert.ThrowsAsync<DevTunnelNotFoundException>(() => CreateClient(cli).GetTunnelAsync("mytunnel.usw2"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MetadataUpdateRetriesAfterRecheckingRemoteState(bool firstUpdateWasApplied)
    {
        var cli = new TestDevTunnelCli();
        cli.EnqueueShowResult(0, TunnelJson(description: "old"));
        cli.EnqueueUpdateResult(1, error: "Transient service error.");
        cli.EnqueueShowResult(0, TunnelJson(description: firstUpdateWasApplied ? "new" : "old"));
        if (!firstUpdateWasApplied)
        {
            cli.EnqueueUpdateResult(0, TunnelJson(description: "new"));
        }
        var result = await CreateClient(cli).CreateTunnelAsync("mytunnel", new() { Description = "new" });
        Assert.Equal("new", result.Description);
        Assert.Equal(firstUpdateWasApplied
            ? new[] { nameof(DevTunnelCli.ShowTunnelAsync), nameof(DevTunnelCli.UpdateTunnelAsync), nameof(DevTunnelCli.ShowTunnelAsync) }
            : [nameof(DevTunnelCli.ShowTunnelAsync), nameof(DevTunnelCli.UpdateTunnelAsync), nameof(DevTunnelCli.ShowTunnelAsync), nameof(DevTunnelCli.UpdateTunnelAsync)],
            cli.Calls.Select(c => c.Method));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AccessMutationRetriesWithoutRepeatingCompletedResets(bool port)
    {
        var cli = new TestDevTunnelCli();
        if (port)
        {
            cli.EnqueueShowPortResult(0, PortJson(access: [AnonymousAccess(deny: true)]));
            cli.EnqueueShowPortResult(0, PortJson());
        }
        else
        {
            cli.EnqueueShowResult(0, TunnelJson(access: [AnonymousAccess(deny: true)]));
            cli.EnqueueShowResult(0, TunnelJson());
        }
        cli.EnqueueResetAccessResult(0, """{"accessControlEntries":[]}""");
        cli.EnqueueCreateAccessResult(1, error: "Transient service error after reset.");
        cli.EnqueueCreateAccessResult(0, """{"accessControlEntries":[]}""");
        var client = CreateClient(cli);
        if (port)
        {
            await client.CreatePortAsync("mytunnel.usw2", 3000, new() { Protocol = "http", Labels = ["label"], AllowAnonymous = true });
        }
        else
        {
            await client.CreateTunnelAsync("mytunnel", new() { AllowAnonymous = true });
        }
        var show = port ? nameof(DevTunnelCli.ShowPortAsync) : nameof(DevTunnelCli.ShowTunnelAsync);
        Assert.Equal([show, nameof(DevTunnelCli.ResetAccessAsync), nameof(DevTunnelCli.CreateAccessAsync), show, nameof(DevTunnelCli.CreateAccessAsync)],
            cli.Calls.Select(c => c.Method));
    }

    [Fact]
    public async Task CompletedAccessMutationIsNotRepeatedAfterLostResponse()
    {
        var cli = new TestDevTunnelCli();
        cli.EnqueueShowResult(0, TunnelJson());
        cli.EnqueueCreateAccessResult(1, error: "Response lost after successful mutation.");
        cli.EnqueueShowResult(0, TunnelJson(access: [AnonymousAccess(deny: false)]));
        await CreateClient(cli).CreateTunnelAsync("mytunnel", new() { AllowAnonymous = true });
        Assert.Equal([nameof(DevTunnelCli.ShowTunnelAsync), nameof(DevTunnelCli.CreateAccessAsync), nameof(DevTunnelCli.ShowTunnelAsync)],
            cli.Calls.Select(c => c.Method));
    }

    [Fact]
    public async Task ProvisioningMutationRetriesAreBounded()
    {
        var cli = new TestDevTunnelCli();
        for (var i = 0; i < 3; i++)
        {
            cli.EnqueueShowResult(0, TunnelJson(description: "old"));
            cli.EnqueueUpdateResult(1, error: "Persistent service error.");
        }
        await Assert.ThrowsAnyAsync<DistributedApplicationException>(() => CreateClient(cli).CreateTunnelAsync("mytunnel", new() { Description = "new" }));
        Assert.Equal(6, cli.Calls.Count);
    }

    [Fact]
    public async Task MissingTunnelIsCreated()
    {
        var cli = new TestDevTunnelCli();
        cli.EnqueueShowResult(DevTunnelCli.ResourceNotFoundExitCode);
        cli.EnqueueCreateResult(0, TunnelJson());
        var result = await CreateClient(cli).CreateTunnelAsync("mytunnel", new());
        Assert.Equal("mytunnel.usw2", result.TunnelId);
        Assert.Equal([nameof(DevTunnelCli.ShowTunnelAsync), nameof(DevTunnelCli.CreateTunnelAsync)], cli.Calls.Select(c => c.Method));
    }

    [Fact]
    public async Task ExistingAnonymousTunnelDoesNotResetMatchingAccess()
    {
        var cli = new TestDevTunnelCli();
        cli.EnqueueShowResult(0, TunnelJson(access: [AnonymousAccess(deny: false)]));
        await CreateClient(cli).CreateTunnelAsync("mytunnel", new() { AllowAnonymous = true });
        Assert.Equal(nameof(DevTunnelCli.ShowTunnelAsync), Assert.Single(cli.Calls).Method);
    }

    [Fact]
    public async Task PrivateTunnelClearsUnexpectedAnonymousAccess()
    {
        var cli = new TestDevTunnelCli();
        cli.EnqueueShowResult(0, TunnelJson(access: [AnonymousAccess(deny: false)]));
        cli.EnqueueResetAccessResult(0, """{"accessControlEntries":[]}""");
        await CreateClient(cli).CreateTunnelAsync("mytunnel", new());
        Assert.Equal([nameof(DevTunnelCli.ShowTunnelAsync), nameof(DevTunnelCli.ResetAccessAsync)], cli.Calls.Select(c => c.Method));
        Assert.All(cli.Calls.Skip(1), c => Assert.Equal("mytunnel.usw2", c.TunnelId));
    }

    [Theory]
    [InlineData("""{"tunnel":{}}""")]
    [InlineData("""{"unexpected":{"tunnelId":"mytunnel.usw2"}}""")]
    public async Task MalformedTunnelIdentityCannotFallBackToDefaultTunnel(string json)
    {
        var cli = new TestDevTunnelCli();
        cli.EnqueueShowResult(0, json);
        await Assert.ThrowsAsync<DistributedApplicationException>(() => CreateClient(cli).CreateTunnelAsync("mytunnel", new()));
        Assert.Single(cli.Calls);
    }

    [Theory]
    [InlineData("mytunnel", "other.usw2")]
    [InlineData("mytunnel", "mytunnel-extra.usw2")]
    [InlineData("mytunnel", "mytunnel.")]
    [InlineData("mytunnel", "mytunnel.usw2.other")]
    [InlineData("mytunnel.usw2", "mytunnel.eun1")]
    [InlineData("mytunnel.usw2", "mytunnel")]
    public async Task UnexpectedTunnelIdentityFailsBeforeMutation(string requested, string returned)
    {
        var cli = new TestDevTunnelCli();
        cli.EnqueueShowResult(0, TunnelJson(tunnelId: returned));
        await Assert.ThrowsAsync<DistributedApplicationException>(() => CreateClient(cli).CreateTunnelAsync(requested, new() { AllowAnonymous = true }));
        Assert.Equal(nameof(DevTunnelCli.ShowTunnelAsync), Assert.Single(cli.Calls).Method);
    }

    [Theory]
    [InlineData("mytunnel", "mytunnel")]
    [InlineData("mytunnel", "mytunnel.usw2")]
    [InlineData("mytunnel.usw2", "MYTUNNEL.USW2")]
    public async Task MatchingTunnelIdentityAllowsClusterResolution(string requested, string returned)
    {
        var cli = new TestDevTunnelCli();
        cli.EnqueueShowResult(0, TunnelJson(tunnelId: returned));
        var result = await CreateClient(cli).CreateTunnelAsync(requested, new());
        Assert.Equal(returned, result.TunnelId);
        Assert.Single(cli.Calls);
    }

    [Theory]
    [InlineData("other.usw2", 3000)]
    [InlineData("mytunnel.eun1", 3000)]
    [InlineData("mytunnel.usw2", 4000)]
    public async Task UnexpectedPortIdentityFailsBeforeMutation(string tunnelId, int portNumber)
    {
        var cli = new TestDevTunnelCli();
        cli.EnqueueShowPortResult(0, PortJson(tunnelId: tunnelId, portNumber: portNumber));
        await Assert.ThrowsAsync<DistributedApplicationException>(() =>
            CreateClient(cli).CreatePortAsync("mytunnel.usw2", 3000, new() { Protocol = "http", Labels = ["label"], AllowAnonymous = true }));
        Assert.Equal(nameof(DevTunnelCli.ShowPortAsync), Assert.Single(cli.Calls).Method);
    }

    [Fact]
    public async Task UnexpectedUpdateIdentityCannotRedirectAccessMutation()
    {
        var cli = new TestDevTunnelCli();
        cli.EnqueueShowResult(0, TunnelJson(description: "old"));
        cli.EnqueueUpdateResult(0, TunnelJson(description: "new", tunnelId: "other.usw2"));
        await Assert.ThrowsAsync<DistributedApplicationException>(() =>
            CreateClient(cli).CreateTunnelAsync("mytunnel", new() { Description = "new", AllowAnonymous = true }));
        Assert.Equal([nameof(DevTunnelCli.ShowTunnelAsync), nameof(DevTunnelCli.UpdateTunnelAsync)], cli.Calls.Select(c => c.Method));
    }

    [Fact]
    public async Task UnexpectedCreatedTunnelIdentityFails()
    {
        var cli = new TestDevTunnelCli();
        cli.EnqueueShowResult(DevTunnelCli.ResourceNotFoundExitCode);
        cli.EnqueueCreateResult(0, TunnelJson(tunnelId: "other.usw2"));
        await Assert.ThrowsAsync<DistributedApplicationException>(() => CreateClient(cli).CreateTunnelAsync("mytunnel", new()));
        Assert.Equal([nameof(DevTunnelCli.ShowTunnelAsync), nameof(DevTunnelCli.CreateTunnelAsync)], cli.Calls.Select(c => c.Method));
    }

    [Fact]
    public async Task UnexpectedCreatedPortIdentityCannotRedirectAccessMutation()
    {
        var cli = new TestDevTunnelCli();
        cli.EnqueueShowPortResult(DevTunnelCli.ResourceNotFoundExitCode);
        cli.EnqueueCreatePortResult(0, PortJson(tunnelId: "other.usw2"));
        await Assert.ThrowsAsync<DistributedApplicationException>(() =>
            CreateClient(cli).CreatePortAsync("mytunnel.usw2", 3000, new() { AllowAnonymous = false }));
        Assert.Equal([nameof(DevTunnelCli.ShowPortAsync), nameof(DevTunnelCli.CreatePortAsync)], cli.Calls.Select(c => c.Method));
    }

    [Fact]
    public async Task StatusQueryRejectsAnotherTunnel()
    {
        var cli = new TestDevTunnelCli();
        cli.EnqueueShowResult(0, TunnelJson(tunnelId: "other.usw2"));
        await Assert.ThrowsAsync<DistributedApplicationException>(() => CreateClient(cli).GetTunnelAsync("mytunnel.usw2"));
    }

    [Theory]
    [InlineData(true, 1, null)]
    [InlineData(false, 1, null)]
    [InlineData(true, 0, "")]
    [InlineData(false, 0, "")]
    [InlineData(true, 0, "null")]
    [InlineData(false, 0, "null")]
    public async Task FreshPortAccessFailureReconcilesWithoutRecreatingPort(bool allowAnonymous, int exitCode, string? response)
    {
        var cli = new TestDevTunnelCli();
        cli.EnqueueShowPortResult(DevTunnelCli.ResourceNotFoundExitCode);
        cli.EnqueueCreatePortResult(0, PortJson());
        cli.EnqueueCreateAccessResult(exitCode, response, "Access mutation did not return success.");
        cli.EnqueueShowPortResult(0, PortJson());
        cli.EnqueueCreateAccessResult(0, """{"accessControlEntries":[]}""");
        await CreateClient(cli).CreatePortAsync("mytunnel.usw2", 3000, new() { Protocol = "http", Labels = ["label"], AllowAnonymous = allowAnonymous });
        Assert.Equal([
            nameof(DevTunnelCli.ShowPortAsync), nameof(DevTunnelCli.CreatePortAsync), nameof(DevTunnelCli.CreateAccessAsync),
            nameof(DevTunnelCli.ShowPortAsync), nameof(DevTunnelCli.CreateAccessAsync)
        ], cli.Calls.Select(c => c.Method));
        Assert.Equal(!allowAnonymous, cli.Calls.Last().Arguments.Contains("--deny"));
    }

    [Fact]
    public async Task MalformedFreshPortAccessResponseCannotReportSuccess()
    {
        var cli = new TestDevTunnelCli();
        cli.EnqueueShowPortResult(DevTunnelCli.ResourceNotFoundExitCode);
        cli.EnqueueCreatePortResult(0, PortJson());
        cli.EnqueueCreateAccessResult(0, "{}");
        await Assert.ThrowsAsync<DistributedApplicationException>(() =>
            CreateClient(cli).CreatePortAsync("mytunnel.usw2", 3000, new() { AllowAnonymous = false }));
        Assert.Equal([nameof(DevTunnelCli.ShowPortAsync), nameof(DevTunnelCli.CreatePortAsync), nameof(DevTunnelCli.CreateAccessAsync)],
            cli.Calls.Select(c => c.Method));
    }

    [Fact]
    public async Task MissingAccessMetadataIsFetchedAndMalformedAccessFailsClosed()
    {
        var cli = new TestDevTunnelCli();
        cli.EnqueueShowResult(0, """{"tunnel":{"tunnelId":"mytunnel.usw2"}}""");
        cli.EnqueueListAccessResult(0, "{}");
        await Assert.ThrowsAsync<DistributedApplicationException>(() => CreateClient(cli).CreateTunnelAsync("mytunnel", new()));
        Assert.Equal([nameof(DevTunnelCli.ShowTunnelAsync), nameof(DevTunnelCli.ListAccessAsync)], cli.Calls.Select(c => c.Method));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(true)]
    [InlineData(false)]
    public async Task MatchingPortIsNotDeletedOrRecreated(bool? anonymous)
    {
        var cli = new TestDevTunnelCli();
        cli.EnqueueShowPortResult(0, PortJson(access: anonymous.HasValue ? [AnonymousAccess(deny: !anonymous.Value)] : []));
        var result = await CreateClient(cli).CreatePortAsync("mytunnel.usw2", 3000, new()
        {
            Protocol = "http",
            Description = "expected",
            Labels = ["label"],
            AllowAnonymous = anonymous
        });
        Assert.Equal(3000, result.PortNumber);
        Assert.Equal("mytunnel.usw2", result.TunnelId);
        Assert.Equal(nameof(DevTunnelCli.ShowPortAsync), Assert.Single(cli.Calls).Method);
    }

    [Fact]
    public async Task PortCanInheritAccessWithoutWritingAnExplicitPolicy()
    {
        var cli = new TestDevTunnelCli();
        cli.EnqueueShowPortResult(0, PortJson(access: [AnonymousAccess(deny: false) with { IsInherited = true }]));
        await CreateClient(cli).CreatePortAsync("mytunnel.usw2", 3000, new() { Protocol = "http", Labels = ["label"] });
        Assert.Equal(nameof(DevTunnelCli.ShowPortAsync), Assert.Single(cli.Calls).Method);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PortAccessDriftIsRepairedWithoutRecreatingPort(bool? anonymous)
    {
        var cli = new TestDevTunnelCli();
        cli.EnqueueShowPortResult(0, PortJson(access: [new("Users", false, false, ["user"], ["connect"])]));
        cli.EnqueueResetAccessResult(0, """{"accessControlEntries":[]}""");
        cli.EnqueueCreateAccessResult(0, """{"accessControlEntries":[]}""");
        await CreateClient(cli).CreatePortAsync("mytunnel.usw2", 3000, new()
        {
            Protocol = "http",
            Labels = ["label"],
            AllowAnonymous = anonymous
        });
        Assert.Equal(anonymous.HasValue
            ? [nameof(DevTunnelCli.ShowPortAsync), nameof(DevTunnelCli.ResetAccessAsync), nameof(DevTunnelCli.CreateAccessAsync)]
            : new[] { nameof(DevTunnelCli.ShowPortAsync), nameof(DevTunnelCli.ResetAccessAsync) },
            cli.Calls.Select(c => c.Method));
        Assert.All(cli.Calls, c => Assert.Equal("mytunnel.usw2", c.TunnelId));
        if (anonymous.HasValue)
        {
            Assert.Equal(!anonymous.Value, cli.Calls.Last().Arguments.Contains("--deny"));
        }
    }

    [Theory]
    [InlineData("https", "expected", "label")]
    [InlineData("http", "changed", "label")]
    [InlineData("http", "expected", "different")]
    public async Task ChangedPortConfigurationIsRecreated(string protocol, string description, string label)
    {
        var cli = new TestDevTunnelCli();
        cli.EnqueueShowPortResult(0, PortJson());
        cli.EnqueueDeletePortResult(0, """{"deletedPort":"mytunnel:3000"}""");
        cli.EnqueueCreatePortResult(0, PortJson(protocol, description, [label]));
        var result = await CreateClient(cli).CreatePortAsync("mytunnel.usw2", 3000, new()
        {
            Protocol = protocol,
            Description = description,
            Labels = [label]
        });
        Assert.Equal(protocol, result.Protocol);
        Assert.Equal([nameof(DevTunnelCli.ShowPortAsync), nameof(DevTunnelCli.DeletePortAsync), nameof(DevTunnelCli.CreatePortAsync)], cli.Calls.Select(c => c.Method));
        Assert.All(cli.Calls, c => Assert.Equal("mytunnel.usw2", c.TunnelId));
    }

    [Fact]
    public async Task MissingPortIsCreatedAndWrappedResponseIsRead()
    {
        var cli = new TestDevTunnelCli();
        cli.EnqueueShowPortResult(DevTunnelCli.ResourceNotFoundExitCode);
        cli.EnqueueCreatePortResult(0, PortJson());
        var result = await CreateClient(cli).CreatePortAsync("mytunnel.usw2", 3000, new() { Protocol = "http" });
        Assert.Equal(3000, result.PortNumber);
        Assert.Equal("mytunnel.usw2", result.TunnelId);
        Assert.Equal([nameof(DevTunnelCli.ShowPortAsync), nameof(DevTunnelCli.CreatePortAsync)], cli.Calls.Select(c => c.Method));
    }

    [Fact]
    public async Task CreateTunnelAsync_WhenCreateConflictsAndUpdateIsNotFound_FailsWithoutRetrying()
    {
        var cli = new TestDevTunnelCli();
        cli.EnqueueShowResult(DevTunnelCli.ResourceNotFoundExitCode);
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
                Assert.Equal(nameof(DevTunnelCli.ShowTunnelAsync), call.Method);
                Assert.Equal("ghost.eun1", call.TunnelId);
            },
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

    private static DevTunnelCliClient CreateClient(TestDevTunnelCli cli) => new(new ConfigurationBuilder().Build(), cli);

    private static DevTunnelAccessStatus.AccessControlEntry AnonymousAccess(bool deny) => new("Anonymous", deny, false, [], ["connect"]);

    private static string TunnelJson(string description = "expected", string expiration = "30 days", DevTunnelAccessStatus.AccessControlEntry[]? access = null, string tunnelId = "mytunnel.usw2") =>
        JsonSerializer.Serialize(new
        {
            tunnel = new
            {
                tunnelId,
                description,
                labels = new[] { "label" },
                tunnelExpiration = expiration,
                accessControl = access ?? []
            }
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web));

    private static string PortJson(string protocol = "http", string description = "expected", string[]? labels = null, DevTunnelAccessStatus.AccessControlEntry[]? access = null, string tunnelId = "mytunnel.usw2", int portNumber = 3000) =>
        JsonSerializer.Serialize(new
        {
            port = new
            {
                tunnelId,
                portNumber,
                protocol,
                description,
                labels = labels ?? ["label"],
                accessControl = access ?? []
            }
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web));
}
