// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Components.Controls;
using Aspire.Dashboard.Components.Tests.Shared;
using Aspire.Dashboard.Model;
using Aspire.Dashboard.Model.Interaction;
using Aspire.Dashboard.Tests.Shared;
using Aspire.DashboardService.Proto.V1;
using Aspire.Tests.Shared.DashboardModel;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.FluentUI.AspNetCore.Components;
using Xunit;

namespace Aspire.Dashboard.Components.Tests.Controls;

[UseCulture("en-US")]
public class MissingParametersPanelTests : DashboardTestContext
{
    [Theory]
    [InlineData(1, "1 parameter needs a value")]
    [InlineData(2, "2 parameters need values")]
    public void Render_ExplainsScopeWithoutInitialErrors(int count, string expectedCount)
    {
        ResourceSetupHelpers.SetupMissingParametersPanel(this);
        var cut = RenderPanel(Enumerable.Range(0, count).Select(i => CreateParameter($"parameter-{i}")).ToArray());

        Assert.Equal(expectedCount, cut.Find(".missing-parameters-count").TextContent);
        Assert.Equal("Complete every required field below, then select Set parameter values to submit all values together.",
            cut.Find(".missing-parameters-description").TextContent);
        Assert.Equal(count, cut.FindComponents<InteractionInputField>().Count);
        Assert.Empty(cut.FindAll(".validation-message"));
        Assert.Empty(cut.FindAll("[aria-invalid='true']"));
        Assert.False(cut.FindComponent<FluentButton>().Instance.Disabled);
    }

    [Theory]
    [InlineData(InputType.Text, false)]
    [InlineData(InputType.SecretText, false)]
    [InlineData(InputType.Choice, false)]
    [InlineData(InputType.Choice, true)]
    [InlineData(InputType.Number, false)]
    [InlineData(InputType.File, false)]
    [InlineData(InputType.Text, false, " ")]
    public async Task Submit_MissingRequiredValue_ShowsAssociatedError(InputType inputType, bool allowCustomChoice, string value = "")
    {
        ResourceSetupHelpers.SetupMissingParametersPanel(this);
        var calls = 0;
        var cut = RenderPanel([CreateParameter("required", inputType, value: value, allowCustomChoice: allowCustomChoice)],
            (_, _, _) => { calls++; return Task.CompletedTask; });
        Assert.Empty(cut.FindAll(".validation-message"));

        await SubmitAsync(cut);

        Assert.Equal(0, calls);
        Assert.Equal("Value required", cut.Find(".validation-message").TextContent);
        var field = cut.FindComponent<InteractionInputField>();
        var input = field.Instance.InputViewModel;
        var control = field.Find(inputType == InputType.File ? "fluent-button" : $"[id='{input.ElementId}']");
        Assert.Equal("true", control.GetAttribute("aria-invalid"));
        Assert.Equal($"{input.ElementId}-description {input.ElementId}-validation", control.GetAttribute("aria-describedby"));
        Assert.Equal(input.ElementId, field.Find("label").GetAttribute("for"));
        Assert.NotNull(field.Find(".input-label-required"));
        if (inputType == InputType.SecretText)
        {
            Assert.Equal(TextInputType.Password, field.FindComponent<FluentTextInput>().Instance.TextInputType);
        }
    }

    [Fact]
    public async Task Submit_PartialValues_ValidatesAllFieldsBeforeExecutingCommands()
    {
        ResourceSetupHelpers.SetupMissingParametersPanel(this);
        var submitted = new List<(string Name, string Value)>();
        var cut = RenderPanel([CreateParameter("first"), CreateParameter("second", InputType.SecretText)],
            (resource, _, options) =>
            {
                submitted.Add((resource.Name, options.Arguments![resource.Name].StringValue));
                return Task.CompletedTask;
            });
        var fields = cut.FindComponents<InteractionInputField>();
        await fields[0].Find("fluent-text-input").TriggerEventAsync("ontextimmediate", new ChangeEventArgs { Value = "first value" });

        await SubmitAsync(cut);

        Assert.Empty(submitted);
        Assert.Empty(cut.FindAll(".missing-parameter-field")[0].QuerySelectorAll(".validation-message"));
        Assert.Equal("Value required", cut.FindAll(".missing-parameter-field")[1].QuerySelector(".validation-message")!.TextContent);

        await fields[1].Find("fluent-text-input").TriggerEventAsync("ontextimmediate", new ChangeEventArgs { Value = "second value" });

        Assert.Empty(cut.FindAll(".validation-message"));
        Assert.Empty(cut.FindAll("[aria-invalid='true']"));

        await SubmitAsync(cut);

        Assert.Equal([("first", "first value"), ("second", "second value")], submitted);
        Assert.Equal(TextInputType.Password, fields[1].FindComponent<FluentTextInput>().Instance.TextInputType);
    }

    [Fact]
    public async Task Submit_Interaction_SubmitsAllValuesOnce()
    {
        ResourceSetupHelpers.SetupMissingParametersPanel(this);
        var commandCalls = 0;
        var cut = RenderPanel([CreateParameter("first"), CreateParameter("second")],
            (_, _, _) => { commandCalls++; return Task.CompletedTask; });
        var response = new WatchInteractionsResponseUpdate
        {
            InteractionId = 1,
            InputsDialog = new InteractionInputsDialog()
        };
        response.InputsDialog.InputItems.Add(new InteractionInput { Name = "first", Label = "First", InputType = InputType.Text, Required = true });
        response.InputsDialog.InputItems.Add(new InteractionInput { Name = "second", Label = "Second", InputType = InputType.Text, Required = true });
        var interactionCalls = 0;
        var interaction = new InteractionsInputsDialogViewModel
        {
            Interaction = response,
            Message = string.Empty,
            DashboardClient = new TestDashboardClient(),
            OnSubmitCallback = (_, _) => { interactionCalls++; return Task.CompletedTask; }
        };
        await cut.InvokeAsync(() => Assert.True(Services.GetRequiredService<ParameterInteractionState>().TrySetCurrent(interaction)));

        await SubmitAsync(cut);
        Assert.Equal(0, interactionCalls);
        Assert.Equal(["Value required", "Value required"], cut.FindAll(".validation-message").Select(message => message.TextContent));
        foreach (var field in cut.FindComponents<InteractionInputField>())
        {
            await field.Find("fluent-text-input").TriggerEventAsync("ontextimmediate",
                new ChangeEventArgs { Value = $"{field.Instance.InputViewModel.Input.Name} value" });
        }

        await SubmitAsync(cut);

        Assert.Equal(1, interactionCalls);
        Assert.Equal(0, commandCalls);
        Assert.Equal(["first value", "second value"], response.InputsDialog.InputItems.Select(input => input.Value));
    }

    [Theory]
    [InlineData(InputType.Text, false, "")]
    [InlineData(InputType.Boolean, true, "")]
    public async Task Submit_OptionalOrBooleanValue_DoesNotRequireNonemptyText(InputType inputType, bool required, string value)
    {
        ResourceSetupHelpers.SetupMissingParametersPanel(this);
        var calls = 0;
        var cut = RenderPanel([CreateParameter("parameter", inputType, required, value)],
            (_, _, _) => { calls++; return Task.CompletedTask; });

        await SubmitAsync(cut);

        Assert.Equal(1, calls);
        Assert.Empty(cut.FindAll(".validation-message"));
    }

    [Fact]
    public void Render_CommandExecuting_DisablesSubmit()
    {
        ResourceSetupHelpers.SetupMissingParametersPanel(this);
        var cut = RenderPanel([CreateParameter("parameter")], isExecuting: true);

        Assert.True(cut.FindComponent<FluentButton>().Instance.Disabled);
    }

    [Fact]
    public async Task EditRequiredText_DoesNotDuplicateValidationOrShowErrorsOnInitialBlur()
    {
        ResourceSetupHelpers.SetupMissingParametersPanel(this);
        var cut = RenderPanel([CreateParameter("parameter")]);
        var input = cut.Find("fluent-text-input");
        await input.FocusInAsync(new());
        await input.FocusOutAsync(new());
        Assert.Empty(cut.FindAll(".validation-message, .fluent-validation-message"));

        await SubmitAsync(cut);
        Assert.Equal("Value required", Assert.Single(cut.FindAll(".validation-message, .fluent-validation-message")).TextContent);

        await input.FocusInAsync(new());
        await input.TriggerEventAsync("ontextimmediate", new ChangeEventArgs { Value = "value" });
        await input.FocusOutAsync(new());
        Assert.Empty(cut.FindAll(".validation-message, .fluent-validation-message"));

        await input.FocusInAsync(new());
        await input.TriggerEventAsync("ontextimmediate", new ChangeEventArgs { Value = "" });
        await input.FocusOutAsync(new());
        Assert.Equal("Value required", Assert.Single(cut.FindAll(".validation-message, .fluent-validation-message")).TextContent);
    }

    [Fact]
    public async Task ChangeParameters_DoesNotShowErrorsBeforeSubmittingNewSelection()
    {
        ResourceSetupHelpers.SetupMissingParametersPanel(this);
        var cut = RenderPanel([CreateParameter("first")]);
        await SubmitAsync(cut);
        Assert.Equal("Value required", cut.Find(".validation-message").TextContent);

        cut.Render(builder => builder.Add(p => p.Parameters, [CreateParameter("second")]));

        Assert.Empty(cut.FindAll(".validation-message"));
        Assert.Empty(cut.FindAll("[aria-invalid='true']"));
        await SubmitAsync(cut);
        Assert.Equal("Value required", cut.Find(".validation-message").TextContent);
    }

    private IRenderedComponent<MissingParametersPanel> RenderPanel(
        IReadOnlyList<ResourceViewModel> parameters,
        Func<ResourceViewModel, CommandViewModel, ExecuteResourceCommandOptions, Task>? execute = null,
        bool isExecuting = false)
    {
        return Render<MissingParametersPanel>(builder => builder
            .Add(p => p.Parameters, parameters)
            .Add(p => p.OnExecuteCommandAsync, (_, _) => Task.CompletedTask)
            .Add(p => p.OnExecuteCommandWithOptionsAsync, execute ?? ((_, _, _) => Task.CompletedTask))
            .Add(p => p.IsCommandExecuting, (_, _) => isExecuting));
    }

    private static Task SubmitAsync(IRenderedComponent<MissingParametersPanel> cut) =>
        cut.Find(".missing-parameters-actions fluent-button").ClickAsync(new());

    private static ResourceViewModel CreateParameter(
        string name,
        InputType inputType = InputType.Text,
        bool required = true,
        string value = "",
        bool allowCustomChoice = false)
    {
        return ModelTestHelpers.CreateResource(
            resourceName: name,
            resourceType: KnownResourceTypes.Parameter,
            state: KnownResourceState.ValueMissing,
            commands: [new CommandViewModel(
                CommandViewModel.SetParameterCommand,
                CommandViewModelState.Enabled,
                displayName: "Set value",
                displayDescription: string.Empty,
                confirmationMessage: string.Empty,
                argumentInputs: [new InteractionInput
                {
                    Name = name,
                    Label = name,
                    Description = $"Description for {name}.",
                    Placeholder = "Enter value",
                    InputType = inputType,
                    Required = required,
                    Value = value,
                    AllowCustomChoice = allowCustomChoice
                }],
                isHighlighted: false,
                iconName: string.Empty,
                iconVariant: Microsoft.FluentUI.AspNetCore.Components.IconVariant.Regular)]);
    }
}
