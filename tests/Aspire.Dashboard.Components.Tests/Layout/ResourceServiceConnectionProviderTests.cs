// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Components.Layout;
using Aspire.Dashboard.Components.Tests.Shared;
using Aspire.Dashboard.Tests.Shared;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Aspire.Dashboard.Components.Tests.Layout;

public class ResourceServiceConnectionProviderTests : DashboardTestContext
{
    [Fact]
    public async Task Disconnected_ShowsStatusAndKeepsPageAvailable()
    {
        FluentUISetupHelpers.AddCommonDashboardServices(this);
        var dashboardClient = new TestDashboardClient(isEnabled: true);
        Services.AddSingleton<IDashboardClient>(dashboardClient);
        Services.AddKeyedSingleton<IDashboardClient>(DashboardClient.LiveAppHostServiceKey, dashboardClient);
        JSInterop.SetupVoid("registerResourceServiceConnectionProvider", _ => true).SetVoidResult();
        JSInterop.SetupVoid("updateResourceServiceConnectionState", _ => true).SetVoidResult();
        var cut = Render<ResourceServiceConnectionProvider>();

        await cut.InvokeAsync(() => dashboardClient.SetConnectionState(DashboardConnectionState.Disconnected));
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("[role=status]")));
        Assert.Empty(cut.FindAll("dialog"));

        await cut.InvokeAsync(() => dashboardClient.SetConnectionState(DashboardConnectionState.Connected));
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("[role=status]")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ViewingHistory_RetryTargetsLiveConnection(bool historySelectedBeforeRender)
    {
        FluentUISetupHelpers.AddCommonDashboardServices(this);
        var selectedClient = new TestDashboardClient(isEnabled: true, isReadOnly: historySelectedBeforeRender);
        var liveClient = new TestDashboardClient(isEnabled: true);
        Services.AddSingleton<IDashboardClient>(selectedClient);
        Services.AddKeyedSingleton<IDashboardClient>(DashboardClient.LiveAppHostServiceKey, liveClient);
        JSInterop.SetupVoid("registerResourceServiceConnectionProvider", _ => true).SetVoidResult();
        JSInterop.SetupVoid("updateResourceServiceConnectionState", _ => true).SetVoidResult();
        var cut = Render<ResourceServiceConnectionProvider>();
        selectedClient.IsReadOnly = true;

        // Historical data remains available even while the independent live AppHost
        // connection is offline. Retry must not be routed through the selected run.
        await cut.InvokeAsync(() => liveClient.SetConnectionState(DashboardConnectionState.Disconnected));
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("[role=status]")));
        await cut.Find("fluent-button").ClickAsync();

        Assert.Equal(1, liveClient.ReconnectCallCount);
        Assert.Equal(0, selectedClient.ReconnectCallCount);
        Assert.True(selectedClient.IsReadOnly);
        Assert.Equal(DashboardConnectionState.Connected, selectedClient.ConnectionState);
    }

    [Fact]
    public async Task ConnectionStateChanged_CanceledJsInterop_DoesNotThrow()
    {
        FluentUISetupHelpers.AddCommonDashboardServices(this);
        var dashboardClient = new TestDashboardClient(isEnabled: true);
        Services.AddSingleton<IDashboardClient>(dashboardClient);
        Services.AddKeyedSingleton<IDashboardClient>(DashboardClient.LiveAppHostServiceKey, dashboardClient);
        JSInterop.SetupVoid("registerResourceServiceConnectionProvider", _ => true).SetVoidResult();
        JSInterop.SetupVoid("updateResourceServiceConnectionState", _ => true).SetException(new TaskCanceledException());
        var cut = Render<ResourceServiceConnectionProvider>();

        await cut.InvokeAsync(() => dashboardClient.SetConnectionState(DashboardConnectionState.Disconnected));

        cut.WaitForAssertion(() => Assert.Single(JSInterop.Invocations, invocation => invocation.Identifier == "updateResourceServiceConnectionState"));
    }
}