// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Components.Dialogs;
using Aspire.Dashboard.Components.Tests.Shared;
using Aspire.Dashboard.Model.Interaction;
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
    public async Task Render_FileChooserUsesFallbackPlaceholderAndScopedBrowseLabel()
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
            var textField = Assert.Single(cut.FindAll("fluent-text-field"));
            Assert.Equal("Choose a file...", textField.GetAttribute("placeholder"));

            var browseButton = Assert.Single(cut.FindAll("fluent-button"), button => button.TextContent.Contains("Browse", StringComparison.Ordinal));
            Assert.Equal("Browse for Artifact", browseButton.GetAttribute("title"));
            Assert.Equal("Browse for Artifact", browseButton.GetAttribute("aria-label"));
        });
    }

    private IRenderedFragment SetUpDialog(out IDialogService dialogService)
    {
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
}
