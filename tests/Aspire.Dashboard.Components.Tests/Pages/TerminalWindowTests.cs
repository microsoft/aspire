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
using Microsoft.JSInterop;
using Xunit;

namespace Aspire.Dashboard.Components.Tests.Pages;

public class TerminalWindowTests : DashboardTestContext
{
    [Fact]
    public async Task WorkloadMetadata_UpdatesWindowTitleAndTitlebar()
    {
        TerminalSetupHelpers.SetupTerminalComponents(this, new TestDashboardClient());
        var head = RenderComponent<HeadOutlet>();
        var cut = RenderComponent<TerminalWindow>(builder => builder.Add(p => p.ResourceName, "shell"));
        var terminal = cut.FindComponent<TerminalView>().Instance;
        var state = new TerminalToolbarState
        {
            TerminalId = 1, Generation = 1, Connected = true,
            Title = "build", WorkingDirectory = "/work/app", ProgressState = "indeterminate"
        };
        await cut.InvokeAsync(() => terminal.OnTerminalStateChanged(state));
        Assert.Equal("build", head.Find("title").TextContent);
        Assert.Equal("build", cut.Find(".terminal-window-titlebar .terminal-title").TextContent);
        Assert.Equal("/work/app", cut.Find(".terminal-window-titlebar .terminal-directory").GetAttribute("data-text"));
        Assert.Single(cut.FindAll(".terminal-window-titlebar [role=progressbar]"));

        await cut.InvokeAsync(() => terminal.OnTerminalStateChanged(state with { Title = "", ProgressState = "none" }));
        Assert.Equal("shell", head.Find("title").TextContent);
        Assert.Equal("shell", cut.Find(".terminal-title").TextContent);
        Assert.Empty(cut.FindAll("[role=progressbar]"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CoordinatedWindow_ChecksGenerationBeforeMountingAndDoesNotRevokeOnDisposal(bool stillDetached)
    {
        TerminalSetupHelpers.SetupTerminalComponents(this, new TestDashboardClient());
        var module = TerminalSetupHelpers.SetupTerminalWindows(this);
        var registration = module.Setup<bool>("registerDetachedTerminalWindow", _ => true);
        Services.GetRequiredService<NavigationManager>().NavigateTo(
            "terminal-window/apphost/terminal?fontSize=23&windowOwner=owner&windowGeneration=generation");
        var cut = RenderComponent<TerminalWindow>(builder => builder.Add(p => p.TerminalId, "terminal"));
        Assert.Empty(cut.FindComponents<TerminalView>());
        Assert.Equal([], JSInterop.Invocations.Where(i => i.Identifier == "initTerminal"));
        Assert.Single(registration.Invocations);

        registration.SetResult(stillDetached);
        cut.WaitForAssertion(() =>
        {
            if (stillDetached)
            {
                Assert.Equal(23, cut.FindComponent<TerminalView>().Instance.InitialFontSize);
            }
            else
            {
                Assert.Empty(cut.FindComponents<TerminalView>());
                Assert.Single(cut.FindAll(".terminal-window-ended"));
            }
        });
        await cut.InvokeAsync(() => cut.Instance.DisposeAsync().AsTask()).DefaultTimeout();
        Assert.Single(module.Invocations, i => i.Identifier ==
            (stillDetached ? "unregisterDetachedTerminalWindow" : "releaseDetachedTerminalWindow"));
        Assert.Equal([], JSInterop.Invocations.Where(i => i.Identifier == "closeTerminalWindow"));
    }

    [Fact]
    public async Task CoordinatedWindow_RevocationRejectsStaleRegistrationAndRemovesCurrentViewer()
    {
        TerminalSetupHelpers.SetupTerminalComponents(this, new TestDashboardClient());
        Services.GetRequiredService<NavigationManager>().NavigateTo(
            "terminal-window/apphost/terminal?windowOwner=owner&windowGeneration=generation");
        var cut = RenderComponent<TerminalWindow>(builder => builder.Add(p => p.TerminalId, "terminal"));
        cut.WaitForAssertion(() => Assert.Single(cut.FindComponents<TerminalView>()));
        var registration = Assert.Single(JSInterop.Invocations, i => i.Identifier == "registerDetachedTerminalWindow");
        var id = Assert.IsType<string>(registration.Arguments[0]);
        await cut.InvokeAsync(() => cut.Instance.OnDetachedTerminalWindowRevokedAsync("obsolete-registration"));
        Assert.Single(cut.FindComponents<TerminalView>());
        await cut.InvokeAsync(() => cut.Instance.OnDetachedTerminalWindowRevokedAsync(id));
        cut.WaitForAssertion(() => Assert.Empty(cut.FindComponents<TerminalView>()));
        Assert.Single(JSInterop.Invocations, i => i.Identifier == "releaseDetachedTerminalWindow");
    }

    [Fact]
    public void CoordinatedWindow_StorageFailureDoesNotMountViewer()
    {
        TerminalSetupHelpers.SetupTerminalComponents(this, new TestDashboardClient());
        var module = TerminalSetupHelpers.SetupTerminalWindows(this);
        module.Setup<bool>("registerDetachedTerminalWindow", _ => true).SetException(new JSException("Storage denied"));
        Services.GetRequiredService<NavigationManager>().NavigateTo(
            "terminal-window/apphost/terminal?windowOwner=owner&windowGeneration=generation");
        var cut = RenderComponent<TerminalWindow>(builder => builder.Add(p => p.TerminalId, "terminal"));
        cut.WaitForAssertion(() => Assert.Equal(Resources.TerminalStrings.TerminalWindowTrackingFailed,
            cut.Find(".terminal-window-ended").TextContent));
        Assert.Empty(cut.FindComponents<TerminalView>());
        Assert.Equal([], JSInterop.Invocations.Where(i => i.Identifier == "initTerminal"));
    }

    [Theory]
    [InlineData("", "terminal", "terminal")]
    [InlineData("/aspire/nested", "terminal", "terminal")]
    [InlineData("", "terminal #1/?%+", "terminal%20%231%2F%3F%25%2B")]
    [InlineData("/aspire/nested", "terminal #1/?%+", "terminal%20%231%2F%3F%25%2B")]
    public void AppHostTerminalEndpoint_UsesDashboardBaseUri(string pathBase, string terminalId, string escapedTerminalId)
    {
        var updates = Channel.CreateUnbounded<WatchTerminalsUpdate>();
        Services.AddSingleton<NavigationManager>(new TestNavigationManager($"https://dashboard.example{pathBase}/"));
        TerminalSetupHelpers.SetupTerminalComponents(this, new TestDashboardClient(terminalChannelProvider: () => updates), pathBase);
        Services.GetRequiredService<NavigationManager>().NavigateTo($"terminal-window/apphost/{escapedTerminalId}");

        var cut = RenderComponent<TerminalWindow>(builder => builder.Add(p => p.TerminalId, terminalId));

        cut.WaitForAssertion(() => TerminalSetupHelpers.AssertSingleTerminalConnection(this,
            $"wss://dashboard.example{pathBase}/api/apphost-terminal?terminalId={escapedTerminalId}"));
    }

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
        cut.WaitForAssertion(() => Assert.Equal("This terminal has ended.", cut.Find(".terminal-window-ended").TextContent));
        cut.SetParametersAndRender(builder => builder.Add(p => p.TerminalId, "terminal"));
        Assert.Equal("This terminal has ended.", cut.Find(".terminal-window-ended").TextContent);
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
        // Starting the background watch does not render, so observe its counters independently.
        await AsyncTestHelpers.AssertIsTrueRetryAsync(
            () => client.TerminalSubscriptionCount == 2 && client.ActiveTerminalSubscriptionCount == 1,
            "The replacement terminal subscription did not start.");
        cut.WaitForAssertion(() =>
        {
            Assert.Equal(2, client.TerminalSubscriptionCount);
            Assert.Equal(1, client.ActiveTerminalSubscriptionCount);
            Assert.Equal("api/apphost-terminal?terminalId=second", cut.FindComponent<TerminalView>().Instance.EndpointPathAndQuery);
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
        await AsyncTestHelpers.AssertIsTrueRetryAsync(
            () => client.TerminalSubscriptionCount == 2,
            "The terminal subscription did not restart after leaving the resource route.");
        Assert.Equal(2, client.TerminalSubscriptionCount);
        await updates.Writer.WriteAsync(TerminalSetupHelpers.Change(TerminalChangeType.Retitled, "next", "Next shell"));
        head.WaitForAssertion(() => Assert.Equal("Next shell", head.Find("title").TextContent));
        Assert.Equal("api/apphost-terminal?terminalId=next", cut.FindComponent<TerminalView>().Instance.EndpointPathAndQuery);
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
            cut.WaitForAssertion(() => Assert.Equal("api/apphost-terminal?terminalId=intermediate",
                cut.FindComponent<TerminalView>().Instance.EndpointPathAndQuery));
            var latestId = returnToFirst ? "first" : "latest";
            var latestRoute = SetTerminalAsync(cut, latestId);
            cut.WaitForAssertion(() => Assert.Equal($"api/apphost-terminal?terminalId={latestId}",
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
            cut.WaitForAssertion(() => Assert.Equal("api/apphost-terminal?terminalId=next",
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
