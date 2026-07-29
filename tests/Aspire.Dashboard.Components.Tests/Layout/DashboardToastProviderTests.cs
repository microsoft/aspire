// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Components.Layout;
using Aspire.Dashboard.Components.Tests.Shared;
using Aspire.Dashboard.Model;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Aspire.Dashboard.Components.Tests.Layout;

public sealed class DashboardToastProviderTests : DashboardTestContext
{
    [Fact]
    public async Task Render_ActionAndDismiss()
    {
        Services.AddLocalization();
        var toastService = new DashboardToastService();
        Services.AddSingleton<IDashboardToastService>(toastService);
        var actionInvoked = false;
        var cut = RenderComponent<DashboardToastProvider>();

        await cut.InvokeAsync(() => toastService.Show(new DashboardToast
        {
            Id = "toast-1",
            Title = "Command started",
            Intent = NotificationIntent.Info,
            IsProgress = true,
            PrimaryAction = new DashboardToastAction
            {
                Text = "Cancel",
                OnClick = () =>
                {
                    actionInvoked = true;
                    return Task.CompletedTask;
                }
            }
        }));

        cut.WaitForAssertion(() => Assert.Contains("Command started", cut.Find(".notif__title").TextContent));

        await cut.InvokeAsync(() => cut.Find(".notif__actions .btn--primary").Click());
        Assert.True(actionInvoked);

        await cut.InvokeAsync(() => cut.Find(".notif__dismiss").Click());
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll(".notif")));
    }

    [Fact]
    public void Service_EvictsOldestToast()
    {
        using var toastService = new DashboardToastService();
        string? closedId = null;
        toastService.OnClose += id => closedId = id;

        for (var i = 1; i <= 4; i++)
        {
            toastService.Show(new DashboardToast
            {
                Id = $"toast-{i}",
                Title = $"Toast {i}",
                Intent = NotificationIntent.Info
            });
        }

        Assert.Equal("toast-1", closedId);
        Assert.Equal(["toast-2", "toast-3", "toast-4"], toastService.GetToasts().Select(t => t.Id));
    }
}
