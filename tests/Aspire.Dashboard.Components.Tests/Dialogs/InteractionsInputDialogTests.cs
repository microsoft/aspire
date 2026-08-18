// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Components.Dialogs;
using Aspire.Dashboard.Components.Resize;
using Aspire.Dashboard.Components.Tests.Shared;
using Aspire.Dashboard.Model;
using Aspire.Dashboard.Model.Interaction;
using Aspire.Dashboard.Tests;
using Aspire.Dashboard.Tests.Shared;
using Aspire.DashboardService.Proto.V1;
using Aspire.Tests.Shared;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.FluentUI.AspNetCore.Components;
using Xunit;

namespace Aspire.Dashboard.Components.Tests.Dialogs;

[UseCulture("en-US")]
public sealed class InteractionsInputDialogTests : DashboardTestContext
{
    [Fact]
    public async Task Render_FileUsesFallbackPlaceholderAndScopedBrowseLabel()
    {
        var getCut = SetUpDialog(out var dialogService);
        var interaction = new WatchInteractionsResponseUpdate
        {
            InteractionId = 1,
            InputsDialog = new InteractionInputsDialog()
        };
        interaction.InputsDialog.InputItems.Add(new InteractionInput
        {
            Name = "artifact",
            Label = "Artifact",
            InputType = InputType.File,
            Placeholder = string.Empty
        });
        var viewModel = new InteractionsInputsDialogViewModel
        {
            Interaction = interaction,
            Message = string.Empty,
            OnSubmitCallback = (_, _) => Task.CompletedTask
        };

        await dialogService.ShowDialogAsync<InteractionsInputDialog>(viewModel, new DialogParameters
        {
            Title = "Upload"
        });
        var cut = getCut();

        cut.WaitForAssertion(() =>
        {
            var browseButton = cut.Find("fluent-button[aria-label='Artifact']");
            Assert.NotNull(browseButton.Id);
            Assert.EndsWith("-FileUploadButton", browseButton.Id);
        });
    }

    [Fact]
    public async Task Render_SecretRevealButton_IsKeyboardFocusable()
    {
        var getCut = SetUpDialog(out var dialogService);

        await dialogService.ShowDialogAsync<InteractionsInputDialog>(CreateSecretTextViewModel(), new DialogParameters
        {
            Title = "Credentials"
        });
        var cut = getCut();

        cut.WaitForAssertion(() =>
        {
            var revealButton = cut.Find(".secret-text-toggle-button");
            Assert.Null(revealButton.GetAttribute("tabindex"));
        });
    }

    [Fact]
    public async Task Render_ActionButtons_DisplaySpecifiedText()
    {
        var getCut = SetUpDialog(out var dialogService);

        await dialogService.ShowDialogAsync<InteractionsInputDialog>(CreateSecretTextViewModel(), new DialogParameters
        {
            Title = "Credentials",
            PrimaryAction = "Continue",
            SecondaryAction = "Go back",
            UseCustomFooter = true
        });
        var cut = getCut();

        cut.WaitForAssertion(() =>
        {
            var buttons = cut.FindAll("fluent-dialog-body [slot='action'] footer fluent-button");
            Assert.Collection(
                buttons,
                button => Assert.Equal("Continue", button.TextContent.Trim()),
                button => Assert.Equal("Go back", button.TextContent.Trim()));

            Assert.Empty(cut.FindAll("fluent-dialog-body + footer"));
        });
    }

    private Func<IRenderedFragment> SetUpDialog(out DashboardDialogService dialogService)
    {
        Services.AddSingleton<IDashboardClient>(new TestDashboardClient());

        FluentUISetupHelpers.SetupDialogInfrastructure(this);
        FluentUISetupHelpers.SetupFluentInputLabel(this);
        FluentUISetupHelpers.SetupFluentTextField(this);
        FluentUISetupHelpers.SetupFluentButton(this);
        FluentUISetupHelpers.SetupFluentInputFile(this);

        var module = JSInterop.SetupModule("./Components/Dialogs/InteractionsInputDialog.razor.js");
        module.SetupVoid("togglePasswordVisibility", _ => true);

        IRenderedFragment? cut = null;
        TestDialogService? testDialogService = null;
        testDialogService = new TestDialogService((content, _) =>
        {
            cut = RenderComponent<CascadingValue<IDialogInstance>>(builder =>
            {
                builder.Add(p => p.Value, testDialogService!.LastInstance!);
                builder.AddChildContent<InteractionsInputDialog>(childBuilder =>
                {
                    childBuilder.Add(p => p.Content, Assert.IsType<InteractionsInputsDialogViewModel>(content));
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

    private static InteractionsInputsDialogViewModel CreateSecretTextViewModel()
    {
        var interaction = new WatchInteractionsResponseUpdate
        {
            InteractionId = 1,
            InputsDialog = new InteractionInputsDialog()
        };
        interaction.InputsDialog.InputItems.Add(new InteractionInput
        {
            Name = "password",
            Label = "Password",
            InputType = InputType.SecretText
        });

        return new InteractionsInputsDialogViewModel
        {
            Interaction = interaction,
            Message = string.Empty,
            OnSubmitCallback = (_, _) => Task.CompletedTask
        };
    }
}
