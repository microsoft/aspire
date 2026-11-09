// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using System.Reflection;
using System.Reflection.Metadata;
using Aspire.Hosting.Watch;
using Aspire.Shared.DotnetWatch;
using Microsoft.AspNetCore.InternalTesting;

namespace Aspire.Hosting.Tests;

[Trait("Partition", "2")]
public class DotnetWatchMetadataUpdateHandlerTests
{
    [Fact]
    public void RegistersTheAppHostMetadataUpdateHandler()
    {
        var handler = Assert.Single(typeof(DistributedApplication).Assembly.GetCustomAttributes<MetadataUpdateHandlerAttribute>());

        Assert.Equal(typeof(DotnetWatchMetadataUpdateHandler), handler.HandlerType);
    }

    [Theory]
    [InlineData(null, null, null, null, null)]
    [InlineData("true", null, "unused", "0123456789abcdef0123456789abcdef", null)]
    [InlineData("true", "ide", "unused", "0123456789abcdef0123456789abcdef", null)]
    [InlineData("true", "cli", "", "0123456789abcdef0123456789abcdef", null)]
    [InlineData("true", "cli", "unused", "", null)]
    [InlineData("true", "cli", "unused", "malformed", null)]
    [InlineData("true", "cli", "unused", "0123456789abcdef0123456789abcdef", "real-ide-session")]
    public async Task OrdinaryUnownedPublishAndIdeUpdatesDoNotOpenAChannel(
        string? watch, string? owner, string? endpoint, string? session, string? ideSession)
    {
        var environment = new Dictionary<string, string?>
        {
            ["AppHost__Run__WatchEnabled"] = watch,
            [DotnetWatchOwnership.OwnerEnvironmentVariable] = owner,
            [DotnetWatchOwnership.AppHostControlEnvironmentVariable] = endpoint,
            [DotnetWatchOwnership.AppHostSessionEnvironmentVariable] = session,
            [KnownConfigNames.ExtensionDebugSessionId] = ideSession
        };

        await DotnetWatchMetadataUpdateHandler.NotifyAsync(
            environment.GetValueOrDefault, TestContext.Current.CancellationToken).DefaultTimeout();
        await DotnetWatchMetadataUpdateHandler.NotifyStartedAsync(
            environment.GetValueOrDefault, TestContext.Current.CancellationToken).DefaultTimeout();
    }

    [Theory]
    [InlineData(DotnetWatchAppHostProtocol.ModelInvalidated, "true")]
    [InlineData(DotnetWatchAppHostProtocol.HostStarted, "true")]
    [InlineData(DotnetWatchAppHostProtocol.ModelInvalidated, "false")]
    [InlineData(DotnetWatchAppHostProtocol.HostStarted, "false")]
    [InlineData(DotnetWatchAppHostProtocol.HostStarted, null)]
    public async Task CliOwnedHostReceivesAProtocolNotificationIndependentOfResourceDefault(int kind, string? resourceWatch)
    {
        await using var server = new DotnetWatchAppHostTestChannel();
        var environment = new Dictionary<string, string?>
        {
            ["AppHost__Run__WatchEnabled"] = resourceWatch,
            [DotnetWatchOwnership.OwnerEnvironmentVariable] = DotnetWatchOwnership.CliOwner,
            [DotnetWatchOwnership.AppHostControlEnvironmentVariable] = server.Endpoint,
            [DotnetWatchOwnership.AppHostSessionEnvironmentVariable] = server.Session,
            [KnownConfigNames.DebugSessionPort] = "localhost:12345",
            [KnownConfigNames.DebugSessionInfo] = "{}",
            [KnownConfigNames.DebugSessionRunMode] = "NoDebug"
        };
        var notification = kind == DotnetWatchAppHostProtocol.HostStarted
            ? DotnetWatchMetadataUpdateHandler.NotifyStartedAsync(environment.GetValueOrDefault, TestContext.Current.CancellationToken)
            : DotnetWatchMetadataUpdateHandler.NotifyAsync(environment.GetValueOrDefault, TestContext.Current.CancellationToken);
        await using var stream = await server.AcceptAsync(TestContext.Current.CancellationToken).DefaultTimeout();
        var message = await DotnetWatchAppHostProtocol.ReadAsync(stream, TestContext.Current.CancellationToken).DefaultTimeout();
        await notification.DefaultTimeout();

        Assert.Equal(DotnetWatchAppHostProtocol.Version, message.Version);
        Assert.Equal(server.Session, message.Session);
        Assert.Equal(kind, message.Kind);
        Assert.StartsWith($"{Environment.ProcessId.ToString(CultureInfo.InvariantCulture)}:", message.Instance);
        Assert.True(DotnetWatchAppHostProtocol.IsInstance(message.Instance));
    }

    [Fact]
    public async Task DuplicateStartupNotificationsHaveTheSameRealProcessIdentity()
    {
        var first = await ReceiveStartupAsync();
        var second = await ReceiveStartupAsync();

        Assert.Equal(first.Instance, second.Instance);
        Assert.NotEqual(first.Session, second.Session);
    }

    [Fact]
    public async Task DisappearingCliDoesNotFaultAMetadataUpdate()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        var environment = new Dictionary<string, string?>
        {
            ["AppHost__Run__WatchEnabled"] = "true",
            [DotnetWatchOwnership.OwnerEnvironmentVariable] = DotnetWatchOwnership.CliOwner,
            [DotnetWatchOwnership.AppHostControlEnvironmentVariable] = $"absent{Guid.NewGuid():N}",
            [DotnetWatchOwnership.AppHostSessionEnvironmentVariable] = Guid.NewGuid().ToString("N")
        };

        await DotnetWatchMetadataUpdateHandler.NotifyAsync(environment.GetValueOrDefault, cancellation.Token).DefaultTimeout();
    }

    private static async Task<DotnetWatchAppHostNotification> ReceiveStartupAsync()
    {
        await using var server = new DotnetWatchAppHostTestChannel();
        var environment = new Dictionary<string, string?>
        {
            ["AppHost__Run__WatchEnabled"] = "true",
            [DotnetWatchOwnership.OwnerEnvironmentVariable] = DotnetWatchOwnership.CliOwner,
            [DotnetWatchOwnership.AppHostControlEnvironmentVariable] = server.Endpoint,
            [DotnetWatchOwnership.AppHostSessionEnvironmentVariable] = server.Session
        };
        var notification = DotnetWatchMetadataUpdateHandler.NotifyStartedAsync(environment.GetValueOrDefault, TestContext.Current.CancellationToken);
        await using var stream = await server.AcceptAsync(TestContext.Current.CancellationToken).DefaultTimeout();
        var message = await DotnetWatchAppHostProtocol.ReadAsync(stream, TestContext.Current.CancellationToken).DefaultTimeout();
        await notification.DefaultTimeout();

        return message;
    }
}
