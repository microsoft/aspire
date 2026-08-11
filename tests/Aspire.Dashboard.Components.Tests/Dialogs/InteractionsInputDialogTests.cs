// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Components.Dialogs;
using Aspire.Dashboard.Components.Tests.Shared;
using Aspire.Dashboard.Model.Interaction;
using Aspire.Dashboard.Tests.Shared;
using Aspire.DashboardService.Proto.V1;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.FluentUI.AspNetCore.Components;
using Xunit;

namespace Aspire.Dashboard.Components.Tests.Dialogs;

[UseCulture("en-US")]
public sealed class InteractionsInputDialogTests : DashboardTestContext
{
    [Fact]
    public async Task Render_FileUsesFallbackPlaceholderAndScopedBrowseLabel()
    {
        var cut = SetUpDialog(out var dialogService);
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
        var cut = SetUpDialog(out var dialogService);

        await dialogService.ShowDialogAsync<InteractionsInputDialog>(CreateSecretTextViewModel(), new DialogParameters
        {
            Title = "Credentials"
        });

        cut.WaitForAssertion(() =>
        {
            var revealButton = cut.Find(".secret-text-toggle-button");
            Assert.Null(revealButton.GetAttribute("tabindex"));
        });
    }

    private IRenderedFragment SetUpDialog(out IDialogService dialogService)
    {
        Services.AddSingleton<IDashboardClient>(new TestDashboardClient());

        FluentUISetupHelpers.SetupDialogInfrastructure(this);
        FluentUISetupHelpers.SetupFluentInputLabel(this);
        FluentUISetupHelpers.SetupFluentTextField(this);
        FluentUISetupHelpers.SetupFluentButton(this);
        FluentUISetupHelpers.SetupFluentInputFile(this);

        var module = JSInterop.SetupModule("./Components/Dialogs/InteractionsInputDialog.razor.js");
        module.SetupVoid("togglePasswordVisibility", _ => true);

        var cut = FluentUISetupHelpers.RenderDialogProvider(this);

        dialogService = Services.GetRequiredService<IDialogService>();
        return cut;
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
