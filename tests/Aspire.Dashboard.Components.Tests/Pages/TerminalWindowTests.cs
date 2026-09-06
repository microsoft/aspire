// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Threading.Channels;
using Aspire.Dashboard.Components.Controls;
using Aspire.Dashboard.Components.Pages;
using Aspire.Dashboard.Components.Tests.Shared;
using Aspire.Dashboard.Tests.Shared;
using Aspire.DashboardService.Proto.V1;
using Bunit;
using Microsoft.AspNetCore.InternalTesting;
using Xunit;

namespace Aspire.Dashboard.Components.Tests.Pages;

public class TerminalWindowTests : DashboardTestContext
{
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
}
