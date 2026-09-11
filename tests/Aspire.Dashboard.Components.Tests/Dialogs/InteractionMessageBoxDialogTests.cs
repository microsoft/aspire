// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Components.Dialogs;
using Aspire.Dashboard.Components.Resize;
using Aspire.Dashboard.Components.Tests.Shared;
using Aspire.Dashboard.Model;
using Aspire.Dashboard.Model.Interaction;
using Aspire.DashboardService.Proto.V1;
using Aspire.Dashboard.Tests;
using Aspire.Tests.Shared;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.FluentUI.AspNetCore.Components;
using Xunit;
using Icons = Microsoft.FluentUI.AspNetCore.Components.Icons;

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
            new InteractionMessageBoxContent { MarkupMessage = "Message", Intent = MessageIntent.None },
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
            new InteractionMessageBoxContent { MarkupMessage = "Waiting", Intent = MessageIntent.None },
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

    [Theory]
    [InlineData(MessageIntent.None, null, Color.Default)]
    [InlineData(MessageIntent.Success, typeof(Icons.Filled.Size24.CheckmarkCircle), Color.Success)]
    [InlineData(MessageIntent.Warning, typeof(Icons.Filled.Size24.Warning), Color.Warning)]
    [InlineData(MessageIntent.Error, typeof(Icons.Filled.Size24.DismissCircle), Color.Error)]
    [InlineData(MessageIntent.Information, typeof(Icons.Filled.Size24.Info), Color.Info)]
    [InlineData(MessageIntent.Confirmation, typeof(Icons.Filled.Size24.QuestionCircle), Color.Success)]
    public async Task Intent_RendersExpectedIcon(MessageIntent intent, Type? expectedIconType, Color expectedColor)
    {
        var getCut = SetUpDialog(out var dialogService);

        await dialogService.ShowDialogAsync<InteractionMessageBoxDialog>(
            new InteractionMessageBoxContent { MarkupMessage = "Message", Intent = intent },
            new DialogParameters { UseCustomFooter = true });
        var cut = getCut();

        var icons = cut.FindComponents<FluentIcon<Icon>>()
            .Where(component => component.Instance.Class == "interaction-message-box-icon")
            .ToList();
        if (expectedIconType is null)
        {
            Assert.Empty(icons);
        }
        else
        {
            var icon = Assert.Single(icons).Instance;
            Assert.IsType(expectedIconType, icon.Value);
            Assert.Equal(expectedColor, icon.Color);
        }
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