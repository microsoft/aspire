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
    [InlineData(false, null, "aspire", true, true)]
    [InlineData(true, null, "aspire", true, true)]
    [InlineData(false, "true", null, true, true)]
    [InlineData(true, "true", null, true, true)]
    [InlineData(false, "false", "aspire", true, true)]
    [InlineData(true, "false", "aspire", true, true)]
    [InlineData(false, "true", "ide", true, true)]
    [InlineData(true, "true", "ide", true, true)]
    [InlineData(false, "invalid", "aspire", true, true)]
    [InlineData(true, "invalid", "aspire", true, true)]
    [InlineData(false, "true", "aspire", false, true)]
    [InlineData(true, "true", "aspire", false, true)]
    [InlineData(false, "true", "aspire", true, false)]
    [InlineData(true, "true", "aspire", true, false)]
    public async Task InactiveOrUnownedUpdatesDoNotOpenAControlConnection(
        bool startup, string? watch, string? owner, bool includePipe, bool includeSession)
    {
        var pipeName = $"ah{Guid.NewGuid():N}"[..16];
        await using var server = new NamedPipeServerStream(pipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        using var connectionCancellation = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
        var connection = server.WaitForConnectionAsync(connectionCancellation.Token);
        var environment = new Dictionary<string, string?>
        {
            ["AppHost__Run__WatchEnabled"] = watch,
            [DotnetWatchConfiguration.LaunchOwner] = owner,
            [DotnetWatchConfiguration.HostControlPipe] = includePipe ? pipeName : "",
            [DotnetWatchConfiguration.HostSession] = includeSession ? "current-generation" : ""
        };

        await (startup
            ? DotnetWatchMetadataUpdateHandler.NotifyStartedAsync(name => environment.GetValueOrDefault(name), timeout.Token)
            : DotnetWatchMetadataUpdateHandler.NotifyAsync(name => environment.GetValueOrDefault(name), timeout.Token)).WaitAsync(timeout.Token);

        // The client can finish writing before the server's accept continuation runs. Let the
        // pending accept settle instead of letting immediate cancellation hide a real connection.
        connectionCancellation.CancelAfter(TimeSpan.FromSeconds(1));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => connection);
        Assert.False(server.IsConnected);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExplicitHostGenerationReceivesTypedVersionedNotifications(bool startup)
    {
        var pipeName = $"ah{Guid.NewGuid():N}"[..16];
        await using var server = new NamedPipeServerStream(pipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        var environment = new Dictionary<string, string?>
        {
            ["AppHost__Run__WatchEnabled"] = "true",
            [DotnetWatchConfiguration.LaunchOwner] = "aspire",
            [DotnetWatchConfiguration.HostControlPipe] = pipeName,
            [DotnetWatchConfiguration.HostSession] = "current-generation"
        };
        var notification = startup
            ? DotnetWatchMetadataUpdateHandler.NotifyStartedAsync(name => environment.GetValueOrDefault(name), timeout.Token)
            : DotnetWatchMetadataUpdateHandler.NotifyAsync(name => environment.GetValueOrDefault(name), timeout.Token);
        await server.WaitForConnectionAsync(timeout.Token);

        var message = await DotnetWatchHostProtocol.ReadAsync(server, timeout.Token);
        await notification.WaitAsync(timeout.Token);

        Assert.Equal(1, message.Version);
        Assert.Equal(startup ? DotnetWatchHostProtocol.HostStarted : DotnetWatchHostProtocol.ModelInvalidated, message.Kind);
        Assert.Equal("current-generation", message.Session);
        if (startup)
        {
            Assert.NotEmpty(message.Instance!);
        }
        else
        {
            Assert.Null(message.Instance);
        }
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

}
