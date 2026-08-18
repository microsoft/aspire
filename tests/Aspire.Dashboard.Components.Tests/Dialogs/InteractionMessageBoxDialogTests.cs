// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Components.Dialogs;
using Aspire.Dashboard.Components.Resize;
using Aspire.Dashboard.Components.Tests.Shared;
using Aspire.Dashboard.Model;
using Aspire.Dashboard.Model.Interaction;
using Aspire.Dashboard.Tests;
using Aspire.Tests.Shared;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.FluentUI.AspNetCore.Components;
using Xunit;

namespace Aspire.Dashboard.Components.Tests.Dialogs;

public sealed class InteractionMessageBoxDialogTests : DashboardTestContext
{
    [Theory]
    [InlineData(0, false, null)]
    [InlineData(1, true, true)]
    public async Task ActionButton_ClosesWithExpectedResult(int buttonIndex, bool expectedCancelled, bool? expectedValue)
    {
        var getCut = SetUpDialog(out var dialogService);
        var reference = await dialogService.ShowDialogAsync<InteractionMessageBoxDialog>(
            new InteractionMessageBoxContent { MarkupMessage = "Message" },
            new DialogParameters
            {
                PrimaryAction = "Continue",
                SecondaryAction = "Cancel",
                UseCustomFooter = true
            });
        var cut = getCut();

        var buttons = cut.FindAll("fluent-dialog-body [slot='action'] footer fluent-button");
        Assert.Collection(
            buttons,
            button => Assert.Equal("Continue", button.TextContent.Trim()),
            button => Assert.Equal("Cancel", button.TextContent.Trim()));

        await buttons[buttonIndex].ClickAsync(new MouseEventArgs());
        var result = await reference.Result;

        Assert.Equal(expectedCancelled, result.Cancelled);
        Assert.Equal(expectedValue, result.Value);
    }

    [Fact]
    public async Task SecondaryActionWithoutPrimary_RendersSecondaryButton()
    {
        var getCut = SetUpDialog(out var dialogService);

        await dialogService.ShowDialogAsync<InteractionMessageBoxDialog>(
            new InteractionMessageBoxContent { MarkupMessage = "Waiting" },
            new DialogParameters
            {
                PrimaryAction = string.Empty,
                SecondaryAction = "Stop waiting",
                UseCustomFooter = true
            });
        var cut = getCut();

        var button = Assert.Single(cut.FindAll("fluent-dialog-body [slot='action'] footer fluent-button"));
        Assert.Equal("Stop waiting", button.TextContent.Trim());
    }

    private Func<IRenderedFragment> SetUpDialog(out DashboardDialogService dialogService)
    {
        FluentUISetupHelpers.SetupDialogInfrastructure(this);
        FluentUISetupHelpers.SetupFluentButton(this);

        IRenderedFragment? cut = null;
        TestDialogService? testDialogService = null;
        testDialogService = new TestDialogService((content, _) =>
        {
            cut = RenderComponent<CascadingValue<IDialogInstance>>(builder =>
            {
                builder.Add(component => component.Value, testDialogService!.LastInstance!);
                builder.AddChildContent<InteractionMessageBoxDialog>(childBuilder =>
                {
                    childBuilder.Add(component => component.Content, Assert.IsType<InteractionMessageBoxContent>(content));
                });
            });
            return Task.CompletedTask;
        });
        Services.RemoveAll<IDialogService>();
        Services.AddSingleton<IDialogService>(testDialogService);

        dialogService = new DashboardDialogService(
            testDialogService,
            new TestStringLocalizer<Aspire.Dashboard.Resources.Dialogs>(),
            Services.GetRequiredService<DimensionManager>());
        return () => cut ?? throw new InvalidOperationException("The dialog was not rendered.");
    }
}