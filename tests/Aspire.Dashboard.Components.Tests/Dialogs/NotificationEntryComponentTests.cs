// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Components.Dialogs;
using Aspire.Dashboard.Components.Tests.Shared;
using Aspire.Dashboard.Model;
using Aspire.Dashboard.Tests;
using Bunit;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Microsoft.FluentUI.AspNetCore.Components;
using Xunit;

namespace Aspire.Dashboard.Components.Tests.Dialogs;

public sealed class NotificationEntryComponentTests : DashboardTestContext
{
    [Fact]
    public async Task PrimaryAction_InvokesActionWithoutClosingNotificationCenter()
    {
        FluentUISetupHelpers.SetupFluentUIComponents(this);
        Services.AddSingleton<IStringLocalizer<Aspire.Dashboard.Resources.Dialogs>>(new TestStringLocalizer<Aspire.Dashboard.Resources.Dialogs>());
        Services.AddSingleton(TimeProvider.System);
        var invoked = false;
        var entry = new NotificationEntry
        {
            Title = "Command completed",
            Intent = MessageBarIntent.Success,
            Timestamp = DateTimeOffset.UtcNow,
            PrimaryAction = new NotificationAction
            {
                Text = "View response",
                OnClick = _ =>
                {
                    invoked = true;
                    return Task.CompletedTask;
                }
            }
        };

        var cut = RenderComponent<NotificationEntryComponent>(builder => builder
            .Add(component => component.Entry, entry));

        await cut.Find(".notification-entry-action").ClickAsync(new MouseEventArgs());

        Assert.True(invoked);
    }
}