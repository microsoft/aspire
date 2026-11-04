// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Threading.Channels;
using Aspire.Dashboard.Components.Layout;
using Aspire.Dashboard.Components.Pages;
using Aspire.Dashboard.Components.Resize;
using Aspire.Dashboard.Components.Tests.Shared;
using Aspire.Dashboard.Model;
using Aspire.Dashboard.Tests.Shared;
using Aspire.Tests.Shared.DashboardModel;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Aspire.Dashboard.Components.Tests.Pages;

[UseCulture("en-US")]
public class ResourceOverviewConsoleTests : DashboardTestContext
{
    [Fact]
    public async Task Selection_DiscardsAlreadyDeliveredBatchFromCanceledResource()
    {
        var viewport = new ViewportInformation(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false);
        var resources = Channel.CreateUnbounded<IReadOnlyList<ResourceViewModelChange>>();
        var apiLogs = Channel.CreateUnbounded<IReadOnlyList<ResourceLogLine>>();
        var cacheLogs = Channel.CreateUnbounded<IReadOnlyList<ResourceLogLine>>();
        var delivered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var disposed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new TestDashboardClient(isEnabled: true,
            initialResources: [ModelTestHelpers.CreateResource("api"), ModelTestHelpers.CreateResource("cache")],
            resourceChannelProvider: () => resources,
            consoleLogsChannelProvider: name => name == "api" ? apiLogs : cacheLogs);
        client.BeforeConsoleLogBatchAsync = async (name, _) =>
        {
            if (name == "api")
            {
                delivered.TrySetResult();
                // Model a delivered batch completing after its subscription was canceled.
                await release.Task;
            }
        };
        client.OnConsoleSubscriptionDisposed = name =>
        {
            if (name == "api")
            {
                disposed.TrySetResult();
            }
        };
        ResourceSetupHelpers.SetupResourceOverview(this, viewport, client);
        var navigation = Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo("/resources/api");
        var cut = Render<ResourcesLayout>(p => p.AddCascadingValue(viewport).Add(p => p.Body, builder =>
        {
            builder.OpenComponent<ResourceOverview>(0);
            builder.AddAttribute(1, nameof(ResourceOverview.ResourceName), "api");
            builder.CloseComponent();
        }));
        cut.WaitForAssertion(() => Assert.True(cut.Instance.IsLoaded));
        await apiLogs.Writer.WriteAsync([new ResourceLogLine(99, "old api output", false)]);
        await delivered.Task.WaitAsync(DefaultWaitTimeout);

        try
        {
            navigation.NavigateTo("/resources/cache");
            cut.Render();
            await cacheLogs.Writer.WriteAsync([new ResourceLogLine(1, "current cache output", false)]);
            cut.WaitForAssertion(() => Assert.Equal(["current cache output"],
                cut.FindAll(".overview-console-line").Select(line => line.TextContent.Trim())));
            release.SetResult();
            await disposed.Task.WaitAsync(DefaultWaitTimeout);
            cut.FindComponent<ResourceOverview>().Render();

            Assert.Equal(["current cache output"],
                cut.FindAll(".overview-console-line").Select(line => line.TextContent.Trim()));
            Assert.Equal("cache", cut.Find(".resource-overview").GetAttribute("data-resource-subscription"));
        }
        finally
        {
            release.TrySetResult();
        }
    }
}
