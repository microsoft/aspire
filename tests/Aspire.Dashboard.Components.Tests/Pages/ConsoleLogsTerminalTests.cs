// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Threading.Channels;
using Aspire.Dashboard.Components.Controls;
using Aspire.Dashboard.Components.Resize;
using Aspire.Dashboard.Components.Tests.Shared;
using Aspire.Dashboard.Model;
using Aspire.Dashboard.Tests.Shared;
using Aspire.Dashboard.Utils;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.FluentUI.AspNetCore.Components;
using Xunit;

namespace Aspire.Dashboard.Components.Tests.Pages;

public partial class ConsoleLogsTests
{
    [Theory]
    [InlineData(KnownResourceState.Running)]
    [InlineData(KnownResourceState.Waiting)]
    [InlineData(KnownResourceState.Finished)]
    public async Task TerminalResource_AlwaysShowsLogsAndConsoleOptions(KnownResourceState state)
    {
        var logs = Channel.CreateUnbounded<IReadOnlyList<ResourceLogLine>>();
        var resource = TerminalSetupHelpers.CreateTerminalResource("shell", state: state);
        var client = new TestDashboardClient(isEnabled: true, initialResources: [resource],
            resourceChannelProvider: () => Channel.CreateUnbounded<IReadOnlyList<ResourceViewModelChange>>(),
            consoleLogsChannelProvider: _ => logs);
        SetupConsoleLogsServices(client);
        JSInterop.SetupVoid("Microsoft.FluentUI.Blazor.Components.Select.Initialize", _ => true).SetVoidResult();
        Services.GetRequiredService<NavigationManager>().NavigateTo(DashboardUrls.ConsoleLogsUrl(resource.Name));
        var viewport = new ViewportInformation(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false);
        Services.GetRequiredService<DimensionManager>().InvokeOnViewportInformationChanged(viewport);
        var cut = Render<Components.Pages.ConsoleLogs>(builder => builder
            .Add(p => p.ResourceName, resource.Name)
            .Add(p => p.ViewportInformation, viewport));

        await logs.Writer.WriteAsync([new ResourceLogLine(1, "startup output", false)]);
        cut.WaitForAssertion(() =>
        {
            var viewer = cut.FindComponent<LogViewer>();
            Assert.Single(viewer.Instance.LogEntries!.GetEntries(), l => l.RawContent == "startup output");
            Assert.Empty(cut.FindComponents<TerminalView>());
            Assert.Equal("console-logs-search", cut.FindComponent<FluentTextInput>().Instance.Name);
            Assert.Equal(
                new[]
                {
                    Resources.ConsoleLogs.DownloadLogs,
                    viewer.Instance.ShowTimestamp ? Resources.ConsoleLogs.ConsoleLogsTimestampHide : Resources.ConsoleLogs.ConsoleLogsTimestampShow,
                    Resources.ConsoleLogs.ConsoleLogsTimestampShowUtc,
                    viewer.Instance.NoWrapLogs ? Resources.ConsoleLogs.ConsoleLogsWrapLogs : Resources.ConsoleLogs.ConsoleLogsNoWrapLogs
                },
                cut.Instance.LogsMenuItemsForTest.Where(i => !i.IsDivider).Select(i => i.Text));
        });
    }
}
