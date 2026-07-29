// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Components.Layout;
using Aspire.Dashboard.Components.Tests.Shared;
using Aspire.Dashboard.Model;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Aspire.Dashboard.Components.Tests.Layout;

public sealed class DashboardMessageProviderTests : DashboardTestContext
{
    [Fact]
    public async Task Render_LinkAndDismiss()
    {
        Services.AddLocalization();
        var messageService = new DashboardMessageService();
        Services.AddSingleton<IDashboardMessageService>(messageService);
        var closed = false;
        var cut = RenderComponent<DashboardMessageProvider>();

        await cut.InvokeAsync(() => messageService.Show(new DashboardMessageOptions
        {
            Title = "Unsecured endpoint",
            Body = "The endpoint is not secured.",
            Intent = NotificationIntent.Warning,
            AllowDismiss = true,
            LinkText = "Learn more",
            LinkUrl = "https://example.com",
            OnClose = () =>
            {
                closed = true;
                return Task.CompletedTask;
            }
        }));

        cut.WaitForAssertion(() =>
        {
            Assert.Contains("Unsecured endpoint", cut.Find(".dashboard-message").TextContent);
            Assert.True(cut.Find(".dashboard-message").ClassList.Contains("dashboard-message--warning"));
            Assert.Equal("https://example.com", cut.Find(".dashboard-message-content a").GetAttribute("href"));
        });

        await cut.InvokeAsync(() => cut.Find(".dashboard-message-dismiss").Click());

        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll(".dashboard-message")));
        Assert.True(closed);
    }
}
