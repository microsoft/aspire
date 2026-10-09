// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Components.Layout;
using Aspire.Dashboard.Components.Tests.Shared;
using Aspire.Dashboard.Tests.Shared;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Aspire.Dashboard.Components.Tests.Layout;

public class ResourceTerminalsAvailabilityProviderTests : DashboardTestContext
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public void DisabledOrHistorical_DoesNotSubscribe(bool enabled, bool readOnly)
    {
        FluentUISetupHelpers.AddCommonDashboardServices(this);
        var client = new TestDashboardClient(isEnabled: enabled, isReadOnly: readOnly);
        Services.AddSingleton<IDashboardClient>(client);
        var cut = Render<ResourceTerminalsAvailabilityProvider>(builder => builder.Add(p => p.AvailabilityChanged, _ => { }));
        Assert.Equal(0, client.ResourceSubscriptionCount);
    }

    [Fact]
    public async Task DisposalDuringConnection_CancelsWithoutStartingWatch()
    {
        FluentUISetupHelpers.AddCommonDashboardServices(this);
        var connected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new TestDashboardClient(isEnabled: true, whenConnected: connected.Task);
        Services.AddSingleton<IDashboardClient>(client);
        var cut = Render<ResourceTerminalsAvailabilityProvider>(builder => builder.Add(p => p.AvailabilityChanged, _ => { }));
        await cut.InvokeAsync(() => cut.Instance.DisposeAsync().AsTask());
        connected.SetResult();
        Assert.Equal(0, client.ResourceSubscriptionCount);
    }
}
