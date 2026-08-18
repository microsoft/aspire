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
    public async Task ConnectionStateChanged_CanceledJsInterop_DoesNotThrow()
    {
        var dashboardClient = new TestDashboardClient(isEnabled: true);
        Services.AddSingleton<IDashboardClient>(dashboardClient);
        JSInterop.SetupVoid("registerResourceServiceConnectionProvider", _ => true).SetVoidResult();
        JSInterop.SetupVoid("updateResourceServiceConnectionState", _ => true).SetException(new TaskCanceledException());
        var cut = RenderComponent<ResourceServiceConnectionProvider>();

        await cut.InvokeAsync(() => dashboardClient.SetConnectionState(DashboardConnectionState.Disconnected));

        cut.WaitForAssertion(() => Assert.Single(JSInterop.Invocations, invocation => invocation.Identifier == "updateResourceServiceConnectionState"));
    }
}