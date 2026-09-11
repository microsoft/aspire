// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Components.Dialogs;
using Aspire.Dashboard.Components.Resize;
using Aspire.Dashboard.Components.Tests.Shared;
using Aspire.Dashboard.Extensions;
using Aspire.Dashboard.Model;
using Aspire.Dashboard.Model.Interaction;
using Aspire.Dashboard.Tests;
using Aspire.Dashboard.Tests.Shared;
using Aspire.DashboardService.Proto.V1;
using Aspire.Tests.Shared;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.FluentUI.AspNetCore.Components;
using Xunit;

namespace Aspire.Dashboard.Components.Tests.Dialogs;

[UseCulture("en-US")]
public sealed class InteractionsInputDialogTests : DashboardTestContext
{
    [Theory]
    [InlineData(InputType.Text, false)]
    [InlineData(InputType.SecretText, false)]
    [InlineData(InputType.Choice, false)]
    [InlineData(InputType.Choice, true)]
    [InlineData(InputType.Boolean, false)]
    [InlineData(InputType.Number, false)]
    [InlineData(InputType.File, false)]
    public async Task Render_FieldIds_AreAssociatedUniqueAndStable(InputType inputType, bool allowCustomChoice)
    {
        var getCut = SetUpDialog(out var dialogService);
        var interaction = new WatchInteractionsResponseUpdate
        {
            InteractionId = 1,
            InputsDialog = new InteractionInputsDialog()
        };
        foreach (var name in new[] { "first", "second" })
        {
            interaction.InputsDialog.InputItems.Add(new InteractionInput
            {
                Name = name,
                Label = name,
                Description = $"Description for {name}.",
                InputType = inputType,
                AllowCustomChoice = allowCustomChoice,
                Required = true
            });
        }
        var viewModel = new InteractionsInputsDialogViewModel
        {
            Interaction = interaction,
            Message = string.Empty,
            DashboardClient = new TestDashboardClient(),
            OnSubmitCallback = (_, _) => Task.CompletedTask
        };

        await dialogService.ShowDialogAsync<InteractionsInputDialog>(viewModel, new DialogParameters { Title = "Inputs" });
        var cut = getCut();
        var fields = cut.FindComponents<InteractionInputField>();
        Assert.Equal(2, fields.Count);

        foreach (var field in fields)
        {
            var fieldId = field.Instance.ForId;
            Assert.False(string.IsNullOrEmpty(fieldId));
            var target = Assert.Single(field.FindAll($"[id='{fieldId}']"));
            var description = field.Find(".input-description");
            Assert.Equal($"{fieldId}-description", description.Id);
            Assert.Contains(description.Id, target.GetAttribute("aria-describedby")!.Split(' '));

            if (inputType != InputType.Boolean)
            {
                Assert.Equal(fieldId, field.Find("label.interaction-input-label").GetAttribute("for"));
                Assert.Equal($"{fieldId}-required", field.Find(".input-label-required").Id);
            }
            if (inputType == InputType.File)
            {
                Assert.Equal(fieldId, field.FindComponent<FluentInputFile>().Instance.AnchorId);
            }
        }

        var initialIds = cut.FindAll(".interaction-input [id]").Select(element => element.Id).ToArray();
        Assert.Equal(initialIds.Length, initialIds.Distinct(StringComparer.Ordinal).Count());

        await cut.InvokeAsync(() => viewModel.OnInteractionUpdated!());

        Assert.Equal(initialIds, cut.FindAll(".interaction-input [id]").Select(element => element.Id).ToArray());
    }

    [Theory]
    [InlineData(InputType.Text, true)]
    [InlineData(InputType.Text, false)]
    [InlineData(InputType.File, true)]
    [InlineData(InputType.File, false)]
    public async Task Render_RequiredField_ExposesAccessibleIndicator(InputType inputType, bool required)
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
            Description = "Choose an artifact.",
            InputType = inputType,
            Required = required
        });
        var viewModel = new InteractionsInputsDialogViewModel
        {
            Interaction = interaction,
            Message = string.Empty,
            DashboardClient = new TestDashboardClient(),
            OnSubmitCallback = (_, _) => Task.CompletedTask
        };

        await dialogService.ShowDialogAsync<InteractionsInputDialog>(viewModel, new DialogParameters { Title = "Artifact" });
        var cut = getCut();

        cut.WaitForAssertion(() =>
        {
            var indicators = cut.FindAll(".input-label-required");
            if (required)
            {
                var indicator = Assert.Single(indicators);
                Assert.Equal("img", indicator.GetAttribute("role"));
                Assert.Equal("A value is required.", indicator.GetAttribute("aria-label"));
                Assert.Null(indicator.GetAttribute("aria-hidden"));
            }
            else
            {
                Assert.Empty(indicators);
            }

            if (inputType == InputType.File)
            {
                var browseButton = cut.Find("fluent-button[aria-label='Artifact']");
                var description = cut.Find(".input-description");
                var expectedDescriptionIds = required ? $"{indicators[0].Id} {description.Id}" : description.Id;
                Assert.Equal(expectedDescriptionIds, browseButton.GetAttribute("aria-describedby"));
            }
        });
    }

    [Theory]
    [InlineData(InputType.Text, false)]
    [InlineData(InputType.SecretText, false)]
    [InlineData(InputType.Choice, false)]
    [InlineData(InputType.Choice, true)]
    [InlineData(InputType.Number, false)]
    [InlineData(InputType.File, false)]
    public async Task Render_RequiredInput_ShowsSingleErrorBeforeDescription(InputType inputType, bool allowCustomChoice)
    {
        var getCut = SetUpDialog(out var dialogService);
        var interaction = new WatchInteractionsResponseUpdate
        {
            InteractionId = 1,
            InputsDialog = new InteractionInputsDialog()
        };
        foreach (var disabled in new[] { false, true })
        {
            interaction.InputsDialog.InputItems.Add(new InteractionInput
            {
                Name = disabled ? "disabled" : "enabled",
                Label = disabled ? "Disabled input" : "Enabled input",
                Description = "Input description",
                InputType = inputType,
                AllowCustomChoice = allowCustomChoice,
                Placeholder = "Enter a value",
                Required = true,
                Disabled = disabled
            });
        }
        var submitCount = 0;
        var viewModel = new InteractionsInputsDialogViewModel
        {
            Interaction = interaction,
            Message = string.Empty,
            DashboardClient = new TestDashboardClient(),
            OnSubmitCallback = (_, _) =>
            {
                submitCount++;
                return Task.CompletedTask;
            }
        };

        await dialogService.ShowDialogAsync<InteractionsInputDialog>(viewModel, new DialogParameters { Title = "Required inputs" });
        var cut = getCut();
        await cut.Find("form").SubmitAsync();

        Assert.Equal(0, submitCount);
        Assert.Equal(2, cut.FindComponent<EditForm>().Instance.EditContext!.GetValidationMessages().Count());
        var fields = cut.FindComponents<InteractionInputField>();
        Assert.Equal(2, fields.Count);
        foreach (var field in fields)
        {
            Assert.Collection(field.FindAll(".validation-message, .fluent-validation-message, .input-description"),
                message => Assert.Equal($"{field.Instance.InputViewModel.Input.Label} is required.", message.TextContent.Trim()),
                description => Assert.Equal("Input description", description.TextContent.Trim()));
            if (inputType != InputType.File)
            {
                var fluentField = Assert.Single(field.FindAll("fluent-field"));
                Assert.Contains("invalid", fluentField.ClassList);
                Assert.Equal(field.Instance.ForId, field.Find("fluent-field > [slot='input']").Id);
            }
        }
    }

    [Theory]
    [InlineData(InputType.Text)]
    [InlineData(InputType.SecretText)]
    public async Task Render_RequiredTextInput_ValidatesOnEditsAndSubmit(InputType inputType)
    {
        var getCut = SetUpDialog(out var dialogService);
        var interaction = new WatchInteractionsResponseUpdate
        {
            InteractionId = 1,
            InputsDialog = new InteractionInputsDialog()
        };
        interaction.InputsDialog.InputItems.Add(new InteractionInput
        {
            Name = "name",
            Label = "Name",
            Description = "Enter your name.",
            InputType = inputType,
            Required = true,
            UpdateStateOnChange = true
        });
        var submitCount = 0;
        var viewModel = new InteractionsInputsDialogViewModel
        {
            Interaction = interaction,
            Message = string.Empty,
            DashboardClient = new TestDashboardClient(),
            OnSubmitCallback = (_, _) =>
            {
                submitCount++;
                return Task.CompletedTask;
            }
        };

        await dialogService.ShowDialogAsync<InteractionsInputDialog>(viewModel, new DialogParameters { Title = "Required input" });
        var cut = getCut();
        Assert.Empty(cut.FindAll(".fluent-validation-message"));

        await cut.Find("fluent-text-input").FocusInAsync(new());
        await cut.Find("fluent-text-input").FocusOutAsync(new());
        Assert.Empty(cut.FindAll(".fluent-validation-message"));
        Assert.Empty(cut.FindAll("fluent-field.invalid"));
        Assert.Equal(string.Empty, cut.Find("fluent-field").TextContent.Trim());
        Assert.Equal(0, submitCount);

        await cut.Find("form").SubmitAsync();
        AssertValidationError();
        Assert.Equal(0, submitCount);

        await cut.Find("fluent-text-input").FocusInAsync(new());
        await cut.Find("fluent-text-input").TriggerEventAsync("ontextimmediate", new ChangeEventArgs { Value = "Alex" });
        await cut.Find("fluent-text-input").FocusOutAsync(new());
        Assert.Empty(cut.FindAll(".fluent-validation-message"));
        Assert.Empty(cut.FindAll("fluent-field.invalid"));
        Assert.Equal(1, submitCount);

        var update = viewModel.Interaction.Clone();
        update.InputsDialog.InputItems[0].ValidationErrors.Add("This name is unavailable.");
        await cut.InvokeAsync(() => viewModel.UpdateInteractionAsync(update));
        await cut.Find("fluent-text-input").FocusInAsync(new());
        await cut.Find("fluent-text-input").FocusOutAsync(new());
        Assert.Equal("This name is unavailable.", Assert.Single(cut.FindAll(".fluent-validation-message")).TextContent.Trim());
        Assert.Single(cut.FindAll("fluent-field.invalid"));
        Assert.Equal(1, submitCount);

        await cut.Find("fluent-text-input").FocusInAsync(new());
        await cut.Find("fluent-text-input").TriggerEventAsync("ontextimmediate", new ChangeEventArgs { Value = "" });
        AssertValidationError();
        Assert.Equal(2, submitCount);

        void AssertValidationError()
        {
            Assert.Equal("Name is required.", Assert.Single(cut.FindAll(".fluent-validation-message")).TextContent.Trim());
            var field = Assert.Single(cut.FindAll("fluent-field.invalid"));
            Assert.Equal("Name is required.", field.TextContent.Trim());
        }
    }

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
            DashboardClient = new TestDashboardClient(),
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
            Assert.Contains("aspire-icon-button", revealButton.ClassList);
            Assert.Contains("aspire-input", cut.Find("fluent-field").ClassList);
        });
    }

    [Theory]
    [InlineData(InputType.SecretText, true, false)]
    [InlineData(InputType.SecretText, false, true)]
    [InlineData(InputType.SecretText, true, true)]
    [InlineData(InputType.File, true, false)]
    [InlineData(InputType.File, false, true)]
    [InlineData(InputType.File, true, true)]
    public async Task Render_DisabledInput_UpdatesAuxiliaryControls(InputType inputType, bool disabled, bool loading)
    {
        var getCut = SetUpDialog(out var dialogService);
        var interaction = new WatchInteractionsResponseUpdate
        {
            InteractionId = 1,
            InputsDialog = new InteractionInputsDialog()
        };
        interaction.InputsDialog.InputItems.Add(new InteractionInput
        {
            Name = "input",
            Label = "Input",
            InputType = inputType
        });
        var viewModel = new InteractionsInputsDialogViewModel
        {
            Interaction = interaction,
            Message = string.Empty,
            DashboardClient = new TestDashboardClient(),
            OnSubmitCallback = (_, _) => Task.CompletedTask
        };

        await dialogService.ShowDialogAsync<InteractionsInputDialog>(viewModel, new DialogParameters { Title = "Input state" });
        var cut = getCut();
        AssertInputState(expectedDisabled: false);

        var update = viewModel.Interaction.Clone();
        update.InputsDialog.InputItems[0].Disabled = disabled;
        update.InputsDialog.InputItems[0].Loading = loading;
        await cut.InvokeAsync(() => viewModel.UpdateInteractionAsync(update));
        AssertInputState(expectedDisabled: true);

        update = viewModel.Interaction.Clone();
        update.InputsDialog.InputItems[0].Disabled = false;
        update.InputsDialog.InputItems[0].Loading = false;
        await cut.InvokeAsync(() => viewModel.UpdateInteractionAsync(update));
        AssertInputState(expectedDisabled: false);

        void AssertInputState(bool expectedDisabled)
        {
            if (inputType == InputType.SecretText)
            {
                Assert.Equal(expectedDisabled, cut.Find("fluent-text-input").HasAttribute("disabled"));
                Assert.Equal(expectedDisabled ? 0 : 1, cut.FindAll(".secret-text-toggle-button").Count);
            }
            else
            {
                Assert.Equal(expectedDisabled, cut.FindComponent<FluentInputFile>().Instance.Disabled);
                Assert.Equal(expectedDisabled, cut.Find("input[type='file']").HasAttribute("disabled"));
                Assert.Equal(expectedDisabled, cut.Find("fluent-button[aria-label='Input']").HasAttribute("disabled"));
            }
        }
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
                button =>
                {
                    Assert.Equal("Continue", button.TextContent.Trim());
                    Assert.Contains("aspire-button", button.ClassList);
                },
                button =>
                {
                    Assert.Equal("Go back", button.TextContent.Trim());
                    Assert.Contains("aspire-button", button.ClassList);
                });

            Assert.Empty(cut.FindAll("fluent-dialog-body + footer"));
        });
    }

    [Fact]
    public async Task Render_CustomChoiceTypingFiltersAndHighlightsOptions()
    {
        var getCut = SetUpDialog(out var dialogService);
        var viewModel = CreateChoiceViewModel(allowCustomChoice: true);

        await dialogService.ShowDialogAsync<InteractionsInputDialog>(viewModel, new DialogParameters
        {
            Title = "Choose a color"
        });
        var cut = getCut();
        var combobox = cut.Find("fluent-dropdown[type='combobox']");

        await combobox.TriggerEventAsync("ontextimmediate", new ChangeEventArgs { Value = "blu" });

        Assert.Equal("blu", viewModel.Inputs[0].Value);
        var option = Assert.Single(combobox.QuerySelectorAll("fluent-option:not([freeform])"));
        Assert.Equal("Blue", option.GetAttribute("text"));
        Assert.Equal("Blu", Assert.Single(option.QuerySelectorAll("mark")).TextContent);

        var component = Assert.Single(cut.FindComponents<FluentCombobox<SelectViewModel<string>, SelectViewModel<string>>>());
        Assert.True(component.Instance.Immediate);
        Assert.Equal(FluentUIExtensions.InputDelay, component.Instance.ImmediateDelay);
        Assert.Null(component.Instance.OptionText!(null));
        await component.InvokeAsync(() => component.Instance.ValueChanged.InvokeAsync(null));
        Assert.Equal("blu", viewModel.Inputs[0].Value);
    }

    [Theory]
    [InlineData("", "custom value")]
    [InlineData("blue", "blu")]
    [InlineData("blue", "")]
    public async Task Render_CustomChoiceTypingThenLosingFocus_PreservesValue(string initialValue, string typedValue)
    {
        var getCut = SetUpDialog(out var dialogService);
        var viewModel = CreateChoiceViewModel(allowCustomChoice: true, initialValue);

        await dialogService.ShowDialogAsync<InteractionsInputDialog>(viewModel, new DialogParameters { Title = "Choose a color" });
        var cut = getCut();

        Assert.Single(cut.FindAll("fluent-dropdown[type='combobox'] fluent-option[freeform] output"));
        await cut.Find("fluent-dropdown[type='combobox']").FocusInAsync(new());
        await cut.Find("fluent-dropdown[type='combobox']").TriggerEventAsync("ontextimmediate", new ChangeEventArgs { Value = typedValue });
        await cut.Find("fluent-dropdown[type='combobox']").FocusOutAsync(new());

        Assert.Equal(typedValue, viewModel.Inputs[0].Value);
        var component = cut.FindComponent<FluentCombobox<SelectViewModel<string>, SelectViewModel<string>>>();
        Assert.Equal(typedValue, component.Instance.ImmediateText);
        Assert.Equal(typedValue, component.Instance.Value?.Name);
    }

    [Fact]
    public async Task Render_CustomChoiceSelectingOption_PreservesKeyAndNotifiesOnce()
    {
        var getCut = SetUpDialog(out var dialogService);
        var updates = new List<(string Value, bool UpdateState)>();
        var viewModel = CreateChoiceViewModel(allowCustomChoice: true, onSubmit: (interaction, updateState) =>
        {
            updates.Add((interaction.InputsDialog.InputItems[0].Value, updateState));
            return Task.CompletedTask;
        });
        viewModel.Inputs[0].UpdateStateOnChange = true;

        await dialogService.ShowDialogAsync<InteractionsInputDialog>(viewModel, new DialogParameters { Title = "Choose a color" });
        var cut = getCut();
        var optionId = cut.Find("fluent-option[text='Blue']").Id;

        await cut.Find("fluent-dropdown[type='combobox']").FocusInAsync(new());
        await cut.Find("fluent-dropdown[type='combobox']").TriggerEventAsync("ondropdownchange", new DropdownEventArgs { SelectedOptions = optionId });
        await cut.Find("fluent-dropdown[type='combobox']").FocusOutAsync(new());

        Assert.Equal("blue", viewModel.Inputs[0].Value);
        Assert.Equal("Blue", cut.FindComponent<FluentCombobox<SelectViewModel<string>, SelectViewModel<string>>>().Instance.ImmediateText);
        Assert.Equal(("blue", true), Assert.Single(updates));
    }

    [Fact]
    public async Task Render_CustomChoiceTyping_ValidatesAndSubmitsValue()
    {
        var getCut = SetUpDialog(out var dialogService);
        var updates = new List<(string Value, bool UpdateState)>();
        var viewModel = CreateChoiceViewModel(allowCustomChoice: true, onSubmit: (interaction, updateState) =>
        {
            updates.Add((interaction.InputsDialog.InputItems[0].Value, updateState));
            return Task.CompletedTask;
        });
        viewModel.Inputs[0].Required = true;
        viewModel.Inputs[0].UpdateStateOnChange = true;

        await dialogService.ShowDialogAsync<InteractionsInputDialog>(viewModel, new DialogParameters { Title = "Choose a color" });
        var cut = getCut();
        var editContext = cut.FindComponent<EditForm>().Instance.EditContext!;

        await cut.Find("form").SubmitAsync();
        Assert.Equal("Color is required.", Assert.Single(editContext.GetValidationMessages()));
        Assert.Equal("Color is required.", Assert.Single(cut.FindAll(".validation-message, .fluent-validation-message")).TextContent.Trim());
        Assert.Single(cut.FindAll("fluent-field.invalid"));
        Assert.Empty(updates);

        await cut.Find("fluent-dropdown[type='combobox']").FocusInAsync(new());
        await cut.Find("fluent-dropdown[type='combobox']").TriggerEventAsync("ontextimmediate", new ChangeEventArgs { Value = "purple" });
        await cut.Find("fluent-dropdown[type='combobox']").TriggerEventAsync("ondropdownchange", new DropdownEventArgs { SelectedOptions = "" });
        await cut.Find("fluent-dropdown[type='combobox']").FocusOutAsync(new());

        Assert.Empty(editContext.GetValidationMessages());
        Assert.Empty(cut.FindAll(".validation-message, .fluent-validation-message"));
        Assert.Empty(cut.FindAll("fluent-field.invalid"));
        Assert.Equal(("purple", true), Assert.Single(updates));

        await cut.Find("form").SubmitAsync();

        Assert.Equal(new[] { ("purple", true), ("purple", false) }, updates);

        await cut.Find("fluent-dropdown[type='combobox']").TriggerEventAsync("ontextimmediate", new ChangeEventArgs { Value = "" });
        await cut.Find("form").SubmitAsync();

        Assert.Equal("Color is required.", Assert.Single(editContext.GetValidationMessages()));
        Assert.Equal("Color is required.", Assert.Single(cut.FindAll(".validation-message, .fluent-validation-message")).TextContent.Trim());
        Assert.Single(cut.FindAll("fluent-field.invalid"));
        Assert.Equal(new[] { ("purple", true), ("purple", false), ("", true) }, updates);
    }

    [Fact]
    public async Task Render_CustomChoiceServerUpdate_PreservesKeyAndValidationErrors()
    {
        var getCut = SetUpDialog(out var dialogService);
        var updates = new List<string>();
        var viewModel = CreateChoiceViewModel(allowCustomChoice: true, "red", onSubmit: (interaction, _) =>
        {
            updates.Add(interaction.InputsDialog.InputItems[0].Value);
            return Task.CompletedTask;
        });
        viewModel.Inputs[0].UpdateStateOnChange = true;
        viewModel.Inputs[0].Loading = true;

        await dialogService.ShowDialogAsync<InteractionsInputDialog>(viewModel, new DialogParameters { Title = "Choose a color" });
        var cut = getCut();

        var update = viewModel.Interaction.Clone();
        update.InputsDialog.InputItems[0].Loading = false;
        update.InputsDialog.InputItems[0].Value = "blue";
        update.InputsDialog.InputItems[0].ValidationErrors.Add("Choose another color.");
        await cut.InvokeAsync(() => viewModel.UpdateInteractionAsync(update));

        var component = cut.FindComponent<FluentCombobox<SelectViewModel<string>, SelectViewModel<string>>>();
        Assert.Equal("blue", viewModel.Inputs[0].Value);
        Assert.Equal("blue", component.Instance.Value?.Id);
        Assert.Equal("Blue", component.Instance.ImmediateText);
        Assert.Equal("Choose another color.", Assert.Single(cut.FindComponent<EditForm>().Instance.EditContext!.GetValidationMessages()));
        Assert.Empty(updates);
    }

    [Theory]
    [InlineData("blue", "Blue")]
    [InlineData("purple", "purple")]
    public async Task Render_CustomChoiceExistingValueInitializesText(string value, string expectedText)
    {
        var getCut = SetUpDialog(out var dialogService);
        var viewModel = CreateChoiceViewModel(allowCustomChoice: true, value);

        await dialogService.ShowDialogAsync<InteractionsInputDialog>(viewModel, new DialogParameters
        {
            Title = "Choose a color"
        });
        var cut = getCut();

        var component = Assert.Single(cut.FindComponents<FluentCombobox<SelectViewModel<string>, SelectViewModel<string>>>());
        Assert.Equal(value, component.Instance.Value?.Id);
        Assert.Equal(value, viewModel.Inputs[0].Value);
        Assert.Equal(expectedText, component.Instance.ImmediateText);
        Assert.Contains(JSInterop.Invocations, invocation =>
            invocation.Identifier == "Microsoft.FluentUI.Blazor.Components.Select.Initialize" &&
            invocation.Arguments.Count == 2 &&
            Equals(invocation.Arguments[1], expectedText));
    }

    [Fact]
    public async Task Render_ChoiceCallbacksAcceptNullOption()
    {
        var getCut = SetUpDialog(out var dialogService);

        var viewModel = CreateChoiceViewModel(allowCustomChoice: false);
        await dialogService.ShowDialogAsync<InteractionsInputDialog>(viewModel, new DialogParameters
        {
            Title = "Choose a color"
        });
        var cut = getCut();

        var component = Assert.Single(cut.FindComponents<FluentSelect<SelectViewModel<string>, string>>());
        Assert.Null(component.Instance.OptionValue!(null));
        Assert.Null(component.Instance.OptionText!(null));
        await component.InvokeAsync(() => component.Instance.ValueChanged.InvokeAsync(null));
        Assert.Equal(string.Empty, viewModel.Inputs[0].Value);
    }

    [Theory]
    [InlineData(InteractionHelpers.MaxFileCount, true)]
    [InlineData(InteractionHelpers.MaxFileCount + 1, false)]
    public async Task Render_MultipleFileSelection_ValidatesMaximumFileCount(int fileCount, bool expectedAccepted)
    {
        var getCut = SetUpDialog(out var dialogService);
        var interaction = new WatchInteractionsResponseUpdate
        {
            InteractionId = 1,
            InputsDialog = new InteractionInputsDialog()
        };
        interaction.InputsDialog.InputItems.Add(new InteractionInput
        {
            Name = "artifacts",
            Label = "Artifacts",
            InputType = InputType.File,
            AllowMultipleFiles = true
        });
        var viewModel = new InteractionsInputsDialogViewModel
        {
            Interaction = interaction,
            Message = string.Empty,
            DashboardClient = new TestDashboardClient(),
            OnSubmitCallback = (_, _) => Task.CompletedTask
        };
        await dialogService.ShowDialogAsync<InteractionsInputDialog>(viewModel, new DialogParameters
        {
            Title = "Upload"
        });
        var cut = getCut();
        var files = Enumerable.Range(0, fileCount)
            .Select(i => (IBrowserFile)new TestBrowserFile($"file-{i}.txt"))
            .ToArray();
        var inputFile = cut.FindComponent<FluentInputFile>();
        var args = new InputFileChangeEventArgs(files);

        if (expectedAccepted)
        {
            await cut.InvokeAsync(() => inputFile.Instance.OnInputFileChange.InvokeAsync(args));

            cut.WaitForAssertion(() => Assert.Equal(fileCount, cut.FindAll(".uploaded-file-container").Count));
        }
        else
        {
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(
                () => cut.InvokeAsync(() => inputFile.Instance.OnInputFileChange.InvokeAsync(args)));

            Assert.Contains(InteractionHelpers.MaxFileCount.ToString(), exception.Message, StringComparison.Ordinal);
        }
    }

    private Func<IRenderedFragment> SetUpDialog(out DashboardDialogService dialogService)
    {
        FluentUISetupHelpers.SetupDialogInfrastructure(this);
        FluentUISetupHelpers.SetupFluentInputLabel(this);
        FluentUISetupHelpers.SetupFluentTextField(this);
        FluentUISetupHelpers.SetupFluentButton(this);
        FluentUISetupHelpers.SetupFluentInputFile(this);
        FluentUISetupHelpers.SetupFluentList(this);
        FluentUISetupHelpers.SetupFluentCombobox(this);

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
            DashboardClient = new TestDashboardClient(),
            OnSubmitCallback = (_, _) => Task.CompletedTask
        };
    }

    private static InteractionsInputsDialogViewModel CreateChoiceViewModel(bool allowCustomChoice, string value = "", Func<WatchInteractionsResponseUpdate, bool, Task>? onSubmit = null)
    {
        var interaction = new WatchInteractionsResponseUpdate
        {
            InteractionId = 1,
            InputsDialog = new InteractionInputsDialog()
        };
        var input = new InteractionInput
        {
            Name = "color",
            Label = "Color",
            InputType = InputType.Choice,
            AllowCustomChoice = allowCustomChoice,
            Value = value
        };
        input.Options.Add("red", "Red");
        input.Options.Add("blue", "Blue");
        interaction.InputsDialog.InputItems.Add(input);

        return new InteractionsInputsDialogViewModel
        {
            Interaction = interaction,
            Message = string.Empty,
            DashboardClient = new TestDashboardClient(),
            OnSubmitCallback = onSubmit ?? ((_, _) => Task.CompletedTask)
        };
    }

    private sealed class TestBrowserFile(string name) : IBrowserFile
    {
        public string Name { get; } = name;
        public DateTimeOffset LastModified { get; } = DateTimeOffset.UnixEpoch;
        public long Size => 0;
        public string ContentType => "text/plain";

        public Stream OpenReadStream(long maxAllowedSize = 512000, CancellationToken cancellationToken = default) => new MemoryStream();
    }
}
