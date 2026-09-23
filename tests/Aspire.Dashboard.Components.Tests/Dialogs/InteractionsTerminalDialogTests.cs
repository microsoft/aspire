// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Components.Controls;
using Aspire.Dashboard.Components.Dialogs;
using Aspire.Dashboard.Components.Tests.Shared;
using Aspire.Dashboard.Model;
using Aspire.Dashboard.Model.Interaction;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.FluentUI.AspNetCore.Components;
using Xunit;

namespace Aspire.Dashboard.Components.Tests.Dialogs;

[UseCulture("en-US")]
public sealed class InteractionsTerminalDialogTests : DashboardTestContext
{
    [Theory]
    [InlineData("", "terminal", "terminal")]
    [InlineData("/aspire/nested", "terminal", "terminal")]
    [InlineData("", "terminal #1/?%+", "terminal%20%231%2F%3F%25%2B")]
    [InlineData("/aspire/nested", "terminal #1/?%+", "terminal%20%231%2F%3F%25%2B")]
    public async Task AppHostTerminalEndpoint_UsesDashboardBaseUri(string pathBase, string terminalId, string escapedTerminalId)
    {
        Services.AddSingleton<NavigationManager>(new TestNavigationManager($"https://dashboard.example{pathBase}/"));
        TerminalSetupHelpers.SetupTerminalView(this, pathBase);
        var getCut = InteractionsSetupHelpers.SetupDialog<InteractionsTerminalDialog, InteractionsTerminalDialogViewModel>(this, p => p.Content, out var dialogService);
        Services.GetRequiredService<NavigationManager>().NavigateTo("consolelogs/resource/other");
        var viewModel = new InteractionsTerminalDialogViewModel { TerminalId = terminalId, Message = "Message" };

        await dialogService.ShowDialogAsync<InteractionsTerminalDialog>(viewModel, new DialogParameters { Title = "Shell" });

        var cut = getCut();
        cut.WaitForAssertion(() => TerminalSetupHelpers.AssertSingleTerminalConnection(this,
            $"wss://dashboard.example{pathBase}/api/apphost-terminal?terminalId={escapedTerminalId}"));
        var terminal = cut.FindComponent<TerminalView>().Instance;
        Assert.True(terminal.Chromeless);
        Assert.True(terminal.AutoFit);
        Assert.False(terminal.ShowDimensionsPicker);
        Assert.False(terminal.ReadOnly);
        Assert.Empty(cut.FindComponents<TerminalWindowButton>());
        Assert.Equal("Message", cut.Find(".interaction-message").TextContent.Trim());

        await cut.InvokeAsync(() => viewModel.UpdateMessageAsync("<strong>Updated</strong>"));
        cut.WaitForAssertion(() => Assert.Equal("Updated", cut.Find(".interaction-message strong").TextContent));
        Assert.Same(terminal, cut.FindComponent<TerminalView>().Instance);
        TerminalSetupHelpers.AssertSingleTerminalConnection(this,
            $"wss://dashboard.example{pathBase}/api/apphost-terminal?terminalId={escapedTerminalId}");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Cancel work")]
    public async Task PrimaryAction_IsOptionalAndAlwaysCancels(string? buttonText)
    {
        TerminalSetupHelpers.SetupTerminalView(this);
        var getCut = InteractionsSetupHelpers.SetupDialog<InteractionsTerminalDialog, InteractionsTerminalDialogViewModel>(this, p => p.Content, out var dialogService);
        var viewModel = new InteractionsTerminalDialogViewModel { TerminalId = "terminal", Message = string.Empty };
        var dialog = await dialogService.ShowDialogAsync<InteractionsTerminalDialog>(viewModel,
            new DialogParameters { PrimaryAction = buttonText, UseCustomFooter = true });
        var cut = getCut();
        var action = cut.FindComponents<FluentButton>().Where(b => b.Instance.Class == "aspire-button aspire-neutral-button").ToList();
        if (string.IsNullOrEmpty(buttonText))
        {
            Assert.Empty(action);
            Assert.False(dialog.Result.IsCompleted);
            await dialog.CloseAsync().DefaultTimeout();
        }
        else
        {
            var button = Assert.Single(action);
            Assert.Equal(buttonText, button.Find("fluent-button").TextContent.Trim());
            await cut.InvokeAsync(button.Instance.OnClick.InvokeAsync);
            var result = await dialog.Result.DefaultTimeout();
            Assert.False(result.Cancelled);
            Assert.Equal("cancel", result.Value);
        }
    }

    [Fact]
    public async Task Dispose_DisconnectsOnlyTheViewerAndUnsubscribesUpdates()
    {
        TerminalSetupHelpers.SetupTerminalView(this);
        var getCut = InteractionsSetupHelpers.SetupDialog<InteractionsTerminalDialog, InteractionsTerminalDialogViewModel>(this, p => p.Content, out var dialogService);
        var viewModel = new InteractionsTerminalDialogViewModel { TerminalId = "terminal", Message = string.Empty };
        var dialog = await dialogService.ShowDialogAsync<InteractionsTerminalDialog>(viewModel, new DialogParameters());
        var cut = getCut();
        cut.WaitForAssertion(() => Assert.Single(JSInterop.Invocations, i => i.Identifier == "initTerminal"));

        // Remove the dialog through the renderer so Blazor disposes its entire component subtree.
        var host = Assert.IsAssignableFrom<IRenderedComponent<CascadingValue<IDialogInstance>>>(cut);
        host.SetParametersAndRender(parameters => parameters
            .Add(p => p.Value, host.Instance.Value)
            .AddChildContent(string.Empty));

        cut.WaitForAssertion(() => Assert.Single(JSInterop.Invocations, i => i.Identifier == "disposeTerminal"));
        Assert.Null(viewModel.OnInteractionUpdated);
        Assert.False(dialog.Result.IsCompleted);
        await viewModel.UpdateMessageAsync("After disposal");
        await dialog.CloseAsync().DefaultTimeout();
    }
}
