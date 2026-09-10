// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Threading.Channels;
using Aspire.Dashboard.Components.Controls;
using Aspire.Dashboard.Components.Pages;
using Aspire.Dashboard.Components.Tests.Shared;
using Aspire.Dashboard.Tests.Shared;
using Aspire.DashboardService.Proto.V1;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Aspire.Dashboard.Components.Tests.Pages;

public class TerminalWindowTests : DashboardTestContext
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OpeningWindow_AutoFitsUsingFontFromQuery(bool appHost)
    {
        var updates = Channel.CreateUnbounded<WatchTerminalsUpdate>();
        TerminalSetupHelpers.SetupTerminalComponents(this, new TestDashboardClient(terminalChannelProvider: () => updates));
        var path = appHost ? "/terminal-window/apphost/terminal" : "/terminal-window/resource/shell/2";
        Services.GetRequiredService<NavigationManager>().NavigateTo($"{path}?fontSize=19");
        var cut = RenderComponent<TerminalWindow>(builder => builder
            .Add(p => p.TerminalId, appHost ? "terminal" : null)
            .Add(p => p.ResourceName, appHost ? null : "shell")
            .Add(p => p.ReplicaIndex, appHost ? 0 : 2));

        var terminal = cut.FindComponent<TerminalView>().Instance;
        Assert.True(terminal.AutoFit);
        Assert.True(terminal.Chromeless);
        Assert.True(terminal.ShowDimensionsPicker);
        Assert.Equal(19, terminal.InitialFontSize);
        var options = Assert.IsType<TerminalViewOptions>(
            Assert.Single(JSInterop.Invocations, i => i.Identifier == "initTerminal").Arguments[3]);
        Assert.True(options.AutoFit);
        Assert.Equal(19, options.InitialFontSize);
    }

    [Fact]
    public async Task SameRoute_PreservesTitleEndedStateAndSubscription()
    {
        var updates = Channel.CreateUnbounded<WatchTerminalsUpdate>();
        var client = new TestDashboardClient(terminalChannelProvider: () => updates);
        TerminalSetupHelpers.SetupTerminalComponents(this, client);
        var head = RenderComponent<HeadOutlet>();
        var cut = RenderComponent<TerminalWindow>(builder => builder.Add(p => p.TerminalId, "terminal"));
        await updates.Writer.WriteAsync(TerminalSetupHelpers.Change(TerminalChangeType.Retitled, "terminal", "Shell"));
        head.WaitForAssertion(() => Assert.Equal("Shell", head.Find("title").TextContent));

        cut.SetParametersAndRender(builder => builder
            .Add(p => p.TerminalId, "terminal")
            .Add(p => p.ResourceName, "unused")
            .Add(p => p.ReplicaIndex, 3));
        Assert.Equal("Shell", head.Find("title").TextContent);
        Assert.Equal(1, client.TerminalSubscriptionCount);

        await updates.Writer.WriteAsync(TerminalSetupHelpers.Change(TerminalChangeType.Removed, "terminal"));
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll(".terminal-window-ended")));
        cut.SetParametersAndRender(builder => builder.Add(p => p.TerminalId, "terminal"));
        Assert.Single(cut.FindAll(".terminal-window-ended"));
        Assert.Equal(1, client.ActiveTerminalSubscriptionCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AppHostRouteChange_ReplacesWatchAndResetsEndedState(bool firstTerminalEnded)
    {
        var firstUpdates = Channel.CreateUnbounded<WatchTerminalsUpdate>();
        var secondUpdates = Channel.CreateUnbounded<WatchTerminalsUpdate>();
        var subscriptions = 0;
        var client = new TestDashboardClient(terminalChannelProvider: () =>
            Interlocked.Increment(ref subscriptions) == 1 ? firstUpdates : secondUpdates);
        TerminalSetupHelpers.SetupTerminalComponents(this, client);
        var head = RenderComponent<HeadOutlet>();
        var cut = RenderComponent<TerminalWindow>(builder => builder.Add(p => p.TerminalId, "first"));
        await firstUpdates.Writer.WriteAsync(TerminalSetupHelpers.Change(TerminalChangeType.Retitled, "first", "First shell"));
        head.WaitForAssertion(() => Assert.Equal("First shell", head.Find("title").TextContent));
        if (firstTerminalEnded)
        {
            await firstUpdates.Writer.WriteAsync(TerminalSetupHelpers.Change(TerminalChangeType.Removed, "first"));
            cut.WaitForAssertion(() => Assert.Single(cut.FindAll(".terminal-window-ended")));
        }

        await SetTerminalAsync(cut, "second").DefaultTimeout();
        cut.WaitForAssertion(() =>
        {
            Assert.Equal(2, client.TerminalSubscriptionCount);
            Assert.Equal(1, client.ActiveTerminalSubscriptionCount);
            Assert.Equal("/api/apphost-terminal?terminalId=second", cut.FindComponent<TerminalView>().Instance.EndpointPathAndQuery);
            Assert.Equal("second", head.Find("title").TextContent);
            Assert.Empty(cut.FindAll(".terminal-window-ended"));
        });
        await secondUpdates.Writer.WriteAsync(TerminalSetupHelpers.Change(TerminalChangeType.Retitled, "second", "Second shell"));
        head.WaitForAssertion(() => Assert.Equal("Second shell", head.Find("title").TextContent));

        await cut.InvokeAsync(() => cut.Instance.DisposeAsync().AsTask()).DefaultTimeout();
        Assert.Equal(0, client.ActiveTerminalSubscriptionCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ResourceRoute_CancelsAppHostWatchAndResetsRouteState(bool firstTerminalEnded)
    {
        var updates = Channel.CreateUnbounded<WatchTerminalsUpdate>();
        var client = new TestDashboardClient(terminalChannelProvider: () => updates);
        TerminalSetupHelpers.SetupTerminalComponents(this, client);
        var head = RenderComponent<HeadOutlet>();
        var cut = RenderComponent<TerminalWindow>(builder => builder.Add(p => p.TerminalId, "terminal"));
        await updates.Writer.WriteAsync(TerminalSetupHelpers.Change(TerminalChangeType.Retitled, "terminal", "Shell"));
        head.WaitForAssertion(() => Assert.Equal("Shell", head.Find("title").TextContent));
        if (firstTerminalEnded)
        {
            await updates.Writer.WriteAsync(TerminalSetupHelpers.Change(TerminalChangeType.Removed, "terminal"));
            cut.WaitForAssertion(() => Assert.Single(cut.FindAll(".terminal-window-ended")));
        }

        cut.SetParametersAndRender(builder => builder
            .Add(p => p.TerminalId, null)
            .Add(p => p.ResourceName, "resource")
            .Add(p => p.ReplicaIndex, 2));
        cut.WaitForAssertion(() =>
        {
            Assert.Equal(0, client.ActiveTerminalSubscriptionCount);
            Assert.Empty(cut.FindAll(".terminal-window-ended"));
            var terminal = cut.FindComponent<TerminalView>().Instance;
            Assert.Null(terminal.EndpointPathAndQuery);
            Assert.Equal("resource", terminal.ResourceName);
            Assert.Equal(2, terminal.ReplicaIndex);
            Assert.Equal("resource #2", head.Find("title").TextContent);
        });

        cut.SetParametersAndRender(builder => builder.Add(p => p.ReplicaIndex, 3));
        Assert.Equal("resource #3", head.Find("title").TextContent);
        Assert.Equal(3, cut.FindComponent<TerminalView>().Instance.ReplicaIndex);
        Assert.Equal(1, client.TerminalSubscriptionCount);

        await SetTerminalAsync(cut, "next").DefaultTimeout();
        cut.WaitForAssertion(() => Assert.Equal(2, client.TerminalSubscriptionCount));
        await updates.Writer.WriteAsync(TerminalSetupHelpers.Change(TerminalChangeType.Retitled, "next", "Next shell"));
        head.WaitForAssertion(() => Assert.Equal("Next shell", head.Find("title").TextContent));
        Assert.Equal("/api/apphost-terminal?terminalId=next", cut.FindComponent<TerminalView>().Instance.EndpointPathAndQuery);
    }

    [Theory]
    [InlineData(false, TerminalChangeType.Removed)]
    [InlineData(false, TerminalChangeType.Retitled)]
    [InlineData(true, TerminalChangeType.Removed)]
    [InlineData(true, TerminalChangeType.Retitled)]
    public async Task RapidRouteChanges_IgnoreOldUpdatesAndOnlyWatchLatest(bool returnToFirst, TerminalChangeType changeType)
    {
        var firstUpdates = Channel.CreateUnbounded<WatchTerminalsUpdate>();
        var latestUpdates = Channel.CreateUnbounded<WatchTerminalsUpdate>();
        var subscriptions = 0;
        var delayedUpdate = TerminalSetupHelpers.Change(changeType, "first", "Stale title");
        var updateReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseUpdate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new TestDashboardClient(terminalChannelProvider: () =>
            Interlocked.Increment(ref subscriptions) == 1 ? firstUpdates : latestUpdates)
        {
            BeforeTerminalUpdateAsync = update =>
            {
                if (ReferenceEquals(update, delayedUpdate))
                {
                    updateReceived.TrySetResult();
                    return releaseUpdate.Task;
                }

                return Task.CompletedTask;
            }
        };
        TerminalSetupHelpers.SetupTerminalComponents(this, client);
        var head = RenderComponent<HeadOutlet>();
        var cut = RenderComponent<TerminalWindow>(builder => builder.Add(p => p.TerminalId, "first"));

        try
        {
            // Hold an already-received update across cancellation, including navigation back to the same ID.
            // This deterministically exercises stale delivery without sleeps or thread-pool timing assumptions.
            await firstUpdates.Writer.WriteAsync(delayedUpdate);
            await updateReceived.Task.DefaultTimeout();
            var intermediateRoute = SetTerminalAsync(cut, "intermediate");
            cut.WaitForAssertion(() => Assert.Equal("/api/apphost-terminal?terminalId=intermediate",
                cut.FindComponent<TerminalView>().Instance.EndpointPathAndQuery));
            var latestId = returnToFirst ? "first" : "latest";
            var latestRoute = SetTerminalAsync(cut, latestId);
            cut.WaitForAssertion(() => Assert.Equal($"/api/apphost-terminal?terminalId={latestId}",
                cut.FindComponent<TerminalView>().Instance.EndpointPathAndQuery));
            Assert.False(intermediateRoute.IsCompleted);
            Assert.False(latestRoute.IsCompleted);
            Assert.Equal(1, client.TerminalSubscriptionCount);
            Assert.Equal(latestId, head.Find("title").TextContent);

            releaseUpdate.TrySetResult();
            await Task.WhenAll(intermediateRoute, latestRoute).DefaultTimeout();
            // The replacement watch starts on a worker and doesn't render until it receives an update.
            // Observe that update before asserting subscription counts, which don't trigger a render themselves.
            await latestUpdates.Writer.WriteAsync(TerminalSetupHelpers.Change(TerminalChangeType.Retitled, latestId, "Latest title"));
            head.WaitForAssertion(() => Assert.Equal("Latest title", head.Find("title").TextContent));
            cut.WaitForAssertion(() =>
            {
                Assert.Equal(2, client.TerminalSubscriptionCount);
                Assert.Equal(1, client.ActiveTerminalSubscriptionCount);
                Assert.Single(cut.FindComponents<TerminalView>());
            });
        }
        finally
        {
            releaseUpdate.TrySetResult();
        }
    }

    [Fact]
    public async Task DisposeDuringRouteChange_JoinsOldWatchWithoutStartingReplacement()
    {
        var updates = Channel.CreateUnbounded<WatchTerminalsUpdate>();
        var updateReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseUpdate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new TestDashboardClient(terminalChannelProvider: () => updates)
        {
            BeforeTerminalUpdateAsync = _ =>
            {
                updateReceived.TrySetResult();
                return releaseUpdate.Task;
            }
        };
        TerminalSetupHelpers.SetupTerminalComponents(this, client);
        var cut = RenderComponent<TerminalWindow>(builder => builder.Add(p => p.TerminalId, "first"));

        try
        {
            await updates.Writer.WriteAsync(TerminalSetupHelpers.Snapshot());
            await updateReceived.Task.DefaultTimeout();
            var routeChange = SetTerminalAsync(cut, "next");
            cut.WaitForAssertion(() => Assert.Equal("/api/apphost-terminal?terminalId=next",
                cut.FindComponent<TerminalView>().Instance.EndpointPathAndQuery));
            var disposal = cut.InvokeAsync(() => cut.Instance.DisposeAsync().AsTask());
            Assert.False(disposal.IsCompleted);

            releaseUpdate.TrySetResult();
            await Task.WhenAll(routeChange, disposal).DefaultTimeout();
            Assert.Equal(0, client.ActiveTerminalSubscriptionCount);
            Assert.Equal(1, client.TerminalSubscriptionCount);
        }
        finally
        {
            releaseUpdate.TrySetResult();
        }
    }

    [Fact]
    public async Task RecoverySnapshot_RemovesMissingTerminalAndDisposalCancelsWatch()
    {
        var updates = Channel.CreateUnbounded<WatchTerminalsUpdate>();
        var client = new TestDashboardClient(terminalChannelProvider: () => updates);
        TerminalSetupHelpers.SetupTerminalComponents(this, client);
        var cut = RenderComponent<TerminalWindow>(builder => builder.Add(p => p.TerminalId, "terminal"));
        await updates.Writer.WriteAsync(TerminalSetupHelpers.Snapshot("terminal"));
        cut.WaitForAssertion(() => Assert.Single(cut.FindComponents<TerminalView>()));

        await updates.Writer.WriteAsync(TerminalSetupHelpers.Snapshot());
        cut.WaitForAssertion(() =>
        {
            Assert.Empty(cut.FindComponents<TerminalView>());
            Assert.Single(cut.FindAll(".terminal-window-ended"));
        });

        await cut.InvokeAsync(() => cut.Instance.DisposeAsync().AsTask()).DefaultTimeout();
        Assert.Equal(0, client.ActiveTerminalSubscriptionCount);
    }

    private static Task SetTerminalAsync(IRenderedComponent<TerminalWindow> component, string terminalId)
        => component.InvokeAsync(() => component.Instance.SetParametersAsync(ParameterView.FromDictionary(
            new Dictionary<string, object?> { [nameof(TerminalWindow.TerminalId)] = terminalId })));
}
