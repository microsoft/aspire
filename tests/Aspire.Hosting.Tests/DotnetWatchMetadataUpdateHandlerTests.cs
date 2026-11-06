// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.IO.Pipes;
using System.Reflection;
using System.Reflection.Metadata;
using Aspire.Hosting.Watch;
using Aspire.Shared;
using Microsoft.AspNetCore.InternalTesting;

namespace Aspire.Hosting.Tests;

public class DotnetWatchMetadataUpdateHandlerTests
{
    [Fact]
    public void RegistersTheMetadataHandlerWithoutChangingOrdinaryMetadataUpdates()
    {
        var handler = Assert.Single(typeof(DistributedApplication).Assembly.GetCustomAttributes<MetadataUpdateHandlerAttribute>());

        Assert.Equal(typeof(DotnetWatchMetadataUpdateHandler), handler.HandlerType);
    }

    [Theory]
    [InlineData(null, null, null, null)]
    [InlineData("true", null, "pipe", "session")]
    [InlineData("false", "aspire", "pipe", "session")]
    [InlineData("true", "ide", "pipe", "session")]
    [InlineData("true", "aspire", "", "session")]
    [InlineData("true", "aspire", "pipe", "")]
    public async Task InactiveOrUnownedUpdatesDoNotOpenAControlConnection(string? watch, string? owner, string? pipe, string? session)
    {
        var environment = new Dictionary<string, string?>
        {
            ["AppHost__Run__WatchEnabled"] = watch,
            [DotnetWatchConfiguration.LaunchOwner] = owner,
            [DotnetWatchConfiguration.HostControlPipe] = pipe,
            [DotnetWatchConfiguration.HostSession] = session
        };

        await DotnetWatchMetadataUpdateHandler.NotifyAsync(
            name => environment.GetValueOrDefault(name), TestContext.Current.CancellationToken).DefaultTimeout();
    }

    [Fact]
    public async Task ExplicitHostGenerationReceivesTypedVersionedInvalidation()
    {
        var pipeName = $"ah{Guid.NewGuid():N}"[..16];
        await using var server = new NamedPipeServerStream(pipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var environment = new Dictionary<string, string?>
        {
            ["AppHost__Run__WatchEnabled"] = "true",
            [DotnetWatchConfiguration.LaunchOwner] = "aspire",
            [DotnetWatchConfiguration.HostControlPipe] = pipeName,
            [DotnetWatchConfiguration.HostSession] = "current-generation"
        };
        var notification = DotnetWatchMetadataUpdateHandler.NotifyAsync(
            name => environment.GetValueOrDefault(name), TestContext.Current.CancellationToken);
        await server.WaitForConnectionAsync(TestContext.Current.CancellationToken).DefaultTimeout();

        var message = await DotnetWatchHostProtocol.ReadAsync(server, TestContext.Current.CancellationToken).DefaultTimeout();
        await notification.DefaultTimeout();

        Assert.Equal(1, message.Version);
        Assert.Equal("current-generation", message.Session);
    }

    [Fact]
    public async Task DisappearingCliDoesNotFaultTheMetadataUpdate()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var environment = new Dictionary<string, string?>
        {
            ["AppHost__Run__WatchEnabled"] = "true",
            [DotnetWatchConfiguration.LaunchOwner] = "aspire",
            [DotnetWatchConfiguration.HostControlPipe] = $"aspire-no-listener-{Guid.NewGuid():N}",
            [DotnetWatchConfiguration.HostSession] = "current-generation"
        };

        await DotnetWatchMetadataUpdateHandler.NotifyAsync(
            name => environment.GetValueOrDefault(name), cancellation.Token).DefaultTimeout();
    }

    [Fact]
    public async Task StartupNotificationIdentifiesTheActualHostForRudeEditGenerationReplacement()
    {
        var pipeName = $"ah{Guid.NewGuid():N}"[..16];
        await using var server = new NamedPipeServerStream(pipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var environment = new Dictionary<string, string?>
        {
            ["AppHost__Run__WatchEnabled"] = "true",
            [DotnetWatchConfiguration.LaunchOwner] = "aspire",
            [DotnetWatchConfiguration.HostControlPipe] = pipeName,
            [DotnetWatchConfiguration.HostSession] = "current-generation"
        };
        var notification = DotnetWatchMetadataUpdateHandler.NotifyStartedAsync(
            name => environment.GetValueOrDefault(name), TestContext.Current.CancellationToken);
        await server.WaitForConnectionAsync(TestContext.Current.CancellationToken).DefaultTimeout();

        var message = await DotnetWatchHostProtocol.ReadAsync(server, TestContext.Current.CancellationToken).DefaultTimeout();
        await notification.DefaultTimeout();

        Assert.Equal(DotnetWatchHostProtocol.HostStarted, message.Kind);
        Assert.NotEmpty(message.Instance!);
        Assert.Equal("current-generation", message.Session);
    }
}
