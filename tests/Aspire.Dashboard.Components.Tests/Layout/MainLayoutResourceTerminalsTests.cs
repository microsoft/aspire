// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Threading.Channels;
using Aspire.Dashboard.Components.Controls;
using Aspire.Dashboard.Components.Layout;
using Aspire.Dashboard.Components.Resize;
using Aspire.Dashboard.Components.Tests.Shared;
using Aspire.Dashboard.Tests.Shared;
using Aspire.Tests.Shared.DashboardModel;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Aspire.Dashboard.Components.Tests.Layout;

public partial class MainLayoutTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ResourceTerminalsNavigation_RunSwitchCancelsAndRestartsAvailabilityWatch(bool isDesktop)
    {
        var disposed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new TestDashboardClient(isEnabled: true,
            initialResources: [TerminalSetupHelpers.CreateTerminalResource("shell")],
            resourceChannelProvider: () => Channel.CreateUnbounded<IReadOnlyList<ResourceViewModelChange>>())
        {
            OnResourceSubscriptionDisposed = () => disposed.TrySetResult()
        };
        var runStore = new FluentUISetupHelpers.TestDashboardRunStore(
        [
            new("current", DashboardRunStore.SchemaVersion, DateTimeOffset.UnixEpoch, null, false, "TestApp", string.Empty, true),
            new("historical", DashboardRunStore.SchemaVersion, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, true, "TestApp", string.Empty, false)
        ]);
        SetupMainLayoutServices(dashboardRunStore: runStore, dashboardClient: client);
        var selection = Assert.IsType<FluentUISetupHelpers.TestDashboardRunSelection>(Services.GetRequiredService<IDashboardRunSelection>());
        selection.OnSelectRun = runId => client.IsReadOnly = runId is not null;
        var cut = Render<MainLayout>(builder => builder.Add(p => p.ViewportInformation,
            new ViewportInformation(IsDesktop: isDesktop, IsUltraLowHeight: false, IsUltraLowWidth: false)));
        cut.WaitForAssertion(() => Assert.True(HasTerminals()));
        Assert.Equal(1, client.ResourceSubscriptionCount);
        await cut.InvokeAsync(() => cut.FindComponent<DashboardRunSelect>().Instance.SelectedRunIdChanged.InvokeAsync("historical"));
        await disposed.Task.WaitAsync(DefaultWaitTimeout);
        Assert.False(HasTerminals());
        Assert.Empty(cut.FindComponents<ResourceTerminalsAvailabilityProvider>());
        await cut.InvokeAsync(() => cut.FindComponent<DashboardRunSelect>().Instance.SelectedRunIdChanged.InvokeAsync(null));
        cut.WaitForAssertion(() => Assert.True(HasTerminals()));
        Assert.Equal(2, client.ResourceSubscriptionCount);

        bool HasTerminals() => isDesktop
            ? cut.FindComponent<DesktopNavMenu>().Instance.HasResourceTerminals
            : cut.FindComponent<MobileNavMenu>().Instance.HasResourceTerminals;
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    public async Task ResourceTerminalsNavigation_TracksCapabilitiesIncludingHiddenResources(bool isDesktop, bool hidden)
    {
        var updates = Channel.CreateUnbounded<IReadOnlyList<ResourceViewModelChange>>();
        var client = new TestDashboardClient(isEnabled: true, resourceChannelProvider: () => updates);
        SetupMainLayoutServices(dashboardClient: client);
        var cut = Render<MainLayout>(builder => builder.Add(p => p.ViewportInformation,
            new ViewportInformation(IsDesktop: isDesktop, IsUltraLowHeight: false, IsUltraLowWidth: false)));
        if (!isDesktop)
        {
            await cut.InvokeAsync(() => cut.Find($"#{MainLayout.NavigationButtonId}").Click());
        }
        AssertNavigation(false);
        var resource = TerminalSetupHelpers.CreateTerminalResource("shell", hidden: hidden);
        await updates.Writer.WriteAsync([new(ResourceViewModelChangeType.Upsert, resource)]);
        cut.WaitForAssertion(() => AssertNavigation(true));
        await updates.Writer.WriteAsync([new(ResourceViewModelChangeType.Upsert, ModelTestHelpers.CreateResource("shell"))]);
        cut.WaitForAssertion(() => AssertNavigation(false));
        await updates.Writer.WriteAsync([new(ResourceViewModelChangeType.Upsert, resource)]);
        cut.WaitForAssertion(() => AssertNavigation(true));
        await updates.Writer.WriteAsync([new(ResourceViewModelChangeType.Delete, resource)]);
        cut.WaitForAssertion(() => AssertNavigation(false));

        void AssertNavigation(bool expected)
        {
            if (isDesktop)
            {
                Assert.Equal(expected, cut.FindComponent<DesktopNavMenu>().Instance.HasResourceTerminals);
            }
            else
            {
                Assert.Equal(expected, cut.FindComponent<MobileNavMenu>().Instance.HasResourceTerminals);
            }
        }
    }
}
