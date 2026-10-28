// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Model;
using Aspire.Dashboard.Extensions;
using Aspire.Dashboard.Model.Interaction;
using Aspire.Dashboard.Model.Markdown;
using Aspire.Dashboard.Utils;
using Aspire.DashboardService.Proto.V1;
using Google.Protobuf.WellKnownTypes;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace Aspire.Dashboard.Components.Controls;

public partial class ParameterOverview : ComponentBase
{
    private const string SaveToUserSecretsName = "SaveToUserSecrets";

    private bool _showJson;
    private string? _resourceName;
    private ExportResult _jsonExport = null!;
    private MarkdownProcessor _markdownProcessor = null!;

    [Inject]
    public required IJSRuntime JS { get; init; }

    [Inject]
    public required IDashboardClient DashboardClient { get; init; }

    [Parameter, EditorRequired]
    public required ResourceViewModel Resource { get; set; }

    [Parameter, EditorRequired]
    public required IDictionary<string, ResourceViewModel> ResourceByName { get; set; }

    [Parameter, EditorRequired]
    public required Func<ResourceViewModel, CommandViewModel, Task> OnExecuteCommandAsync { get; set; }

    [Parameter, EditorRequired]
    public required Func<ResourceViewModel, CommandViewModel, ExecuteResourceCommandOptions, Task> OnExecuteCommandWithOptionsAsync { get; set; }

    [Parameter, EditorRequired]
    public required Func<ResourceViewModel, CommandViewModel, bool> IsCommandExecuting { get; set; }

    private string? ParameterValue { get; set; }
    private bool IsSensitive { get; set; }
    private bool IsUnresolved { get; set; }
    private CommandViewModel? SetCommand { get; set; }
    private CommandViewModel? DeleteCommand { get; set; }
    private InputViewModel? ValueInputViewModel { get; set; }
    private InputViewModel? SaveInputViewModel { get; set; }
    private TextVisualizerViewModel JsonViewModel { get; set; } = null!;
    private bool CanShowJson => _showJson;
    private bool IsSetCommandDisabled => DashboardClient.IsReadOnly ||
        SetCommand is null ||
        SetCommand.State == CommandViewModelState.Disabled ||
        IsCommandExecuting(Resource, SetCommand);
    private bool IsDeleteCommandDisabled => DashboardClient.IsReadOnly ||
        DeleteCommand is null ||
        DeleteCommand.State == CommandViewModelState.Disabled ||
        IsCommandExecuting(Resource, DeleteCommand);
    private bool IsValueSubmitDisabled => IsSetCommandDisabled ||
        ValueInputViewModel is null ||
        (ValueInputViewModel.Input.Required &&
            ValueInputViewModel.Input.InputType != InputType.Boolean &&
            string.IsNullOrWhiteSpace(ValueInputViewModel.Value));

    protected override void OnInitialized()
    {
        _markdownProcessor = InteractionMarkdownHelper.CreateProcessor(ControlsStringsLoc);
    }

    protected override void OnParametersSet()
    {
        var resourceChanged = !string.Equals(_resourceName, Resource.Name, StringComparison.Ordinal);
        _resourceName = Resource.Name;

        IsUnresolved = Resource.HasMissingParameterValueState();
        ParameterValue = null;
        IsSensitive = false;
        SetCommand = Resource.Commands.FirstOrDefault(command =>
            string.Equals(command.Name, CommandViewModel.SetParameterCommand, StringComparisons.CommandName) &&
            command.State != CommandViewModelState.Hidden);
        // The host only shows the delete command while the parameter has a value.
        DeleteCommand = Resource.Commands.FirstOrDefault(command =>
            string.Equals(command.Name, CommandViewModel.DeleteParameterCommand, StringComparisons.CommandName) &&
            command.State != CommandViewModelState.Hidden);
        var valueInput = SetCommand?.ArgumentInputs.FirstOrDefault(input =>
            !string.Equals(input.Name, SaveToUserSecretsName, StringComparisons.InteractionInputName));
        var saveInput = SetCommand?.ArgumentInputs.FirstOrDefault(input =>
            string.Equals(input.Name, SaveToUserSecretsName, StringComparisons.InteractionInputName));

        if (Resource.Properties.TryGetValue(KnownProperties.Parameter.Value, out var property))
        {
            IsSensitive = property.IsValueSensitive;
            if (!IsUnresolved && property.Value.HasStringValue)
            {
                ParameterValue = property.Value.StringValue;
            }
        }

        if (resourceChanged)
        {
            _showJson = false;
            ValueInputViewModel = valueInput is null ? null : new InputViewModel(valueInput);
            SaveInputViewModel = saveInput is null ? null : new InputViewModel(saveInput);
            if (ValueInputViewModel is not null && ParameterValue is not null)
            {
                ValueInputViewModel.Value = ParameterValue;
            }
        }
        else
        {
            if (valueInput is not null)
            {
                ValueInputViewModel?.SetInput(valueInput);
            }
            if (saveInput is not null)
            {
                SaveInputViewModel?.SetInput(saveInput);
            }
        }

        _jsonExport = ExportHelpers.GetResourceAsJson(Resource, ResourceByName);
        JsonViewModel = new TextVisualizerViewModel(_jsonExport.Content, indentText: true, DashboardUIHelpers.JsonFormat);
    }

    private void ToggleJson()
    {
        _showJson = !_showJson;
    }

    private async Task SetValueAsync()
    {
        if (SetCommand is null || ValueInputViewModel is null)
        {
            return;
        }

        if (ValueInputViewModel.Input.InputType == InputType.File)
        {
            await OnExecuteCommandAsync(Resource, SetCommand);
            return;
        }

        if (IsValueSubmitDisabled)
        {
            return;
        }

        var arguments = new Dictionary<string, Value>
        {
            [ValueInputViewModel.Input.Name] = GetArgumentValue(ValueInputViewModel)
        };
        if (SaveInputViewModel is not null)
        {
            arguments[SaveInputViewModel.Input.Name] = Value.ForBool(SaveInputViewModel.IsChecked);
        }

        await OnExecuteCommandWithOptionsAsync(Resource, SetCommand, new ExecuteResourceCommandOptions
        {
            Arguments = arguments,
            NonInteractive = true
        });
    }

    private async Task DeleteValueAsync()
    {
        if (DeleteCommand is null || IsDeleteCommandDisabled)
        {
            return;
        }

        // The delete command asks whether to also remove the value from user secrets, so it runs through the
        // regular command path, which prompts for its arguments.
        await OnExecuteCommandAsync(Resource, DeleteCommand);
    }

    private static Value GetArgumentValue(InputViewModel input)
    {
        return input.Input.InputType switch
        {
            InputType.Boolean => Value.ForBool(input.IsChecked),
            InputType.Number when input.NumberValue is { } number => Value.ForNumber(number),
            _ => Value.ForString(input.Value)
        };
    }

    private void OnChoiceTextChanged(string? text)
    {
        if (ValueInputViewModel is not { } input || input.SelectedOption?.Name == text)
        {
            return;
        }

        text ??= string.Empty;
        input.SelectedOption = input.SelectOptions.FirstOrDefault(option => option.Name == text)
            ?? new SelectViewModel<string> { Id = text, Name = text };
        UpdateChoiceValueState();
    }

    private void UpdateChoiceValueState()
    {
        if (ValueInputViewModel is not { } input)
        {
            return;
        }

        input.SelectedOption ??= input.SelectOptions.FirstOrDefault(option => option.Id == input.Value)
            ?? new SelectViewModel<string> { Id = input.Value, Name = input.Value };
        input.Value = input.SelectedOption.Id;
    }

    private Task DownloadJsonAsync()
    {
        return JS.DownloadFileAsync(_jsonExport.FileName, JsonViewModel.FormattedText);
    }
}
