// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using Aspire.Dashboard.Extensions;
using Aspire.Dashboard.Model;
using Aspire.Dashboard.Model.Interaction;
using Aspire.Dashboard.Model.Markdown;
using Aspire.Dashboard.Resources;
using Aspire.Dashboard.Utils;
using Aspire.DashboardService.Proto.V1;
using Google.Protobuf.WellKnownTypes;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.Localization;

namespace Aspire.Dashboard.Components.Controls;

public partial class MissingParametersPanel : IAsyncDisposable
{
    private const string SaveToUserSecretsName = ParameterInteractionState.SaveToUserSecretsInputName;

    private readonly CancellationTokenSource _disposalCts = new();
    private ParameterInteractionState.PanelRegistration? _registration;
    private IReadOnlyList<ParameterEditor> _editors = [];
    private InputViewModel? _saveInput;
    private bool _saveToUserSecrets;

    [Inject]
    public required IStringLocalizer<Resources.Resources> Loc { get; init; }

    [Inject]
    public required IStringLocalizer<ControlsStrings> ControlsStringsLoc { get; init; }

    [Inject]
    public required IStringLocalizer<Resources.Dialogs> DialogsLoc { get; init; }

    [Inject]
    public required ParameterInteractionState ParameterInteractionState { get; init; }

    [Parameter, EditorRequired]
    public required IReadOnlyList<ResourceViewModel> Parameters { get; set; }

    [Parameter, EditorRequired]
    public required Func<ResourceViewModel, CommandViewModel, Task> OnExecuteCommandAsync { get; set; }

    [Parameter, EditorRequired]
    public required Func<ResourceViewModel, CommandViewModel, ExecuteResourceCommandOptions, Task> OnExecuteCommandWithOptionsAsync { get; set; }

    [Parameter, EditorRequired]
    public required Func<ResourceViewModel, CommandViewModel, bool> IsCommandExecuting { get; set; }

    private IReadOnlyList<ParameterEditor> Editors => _editors;

    private MarkdownProcessor MarkdownProcessor { get; set; } = null!;

    // With the AppHost's interaction, the choice is the interaction's own input. Without it, the choice is passed to
    // each parameter's command.
    private bool SaveToUserSecrets
    {
        get => _saveInput?.IsChecked ?? _saveToUserSecrets;
        set
        {
            if (_saveInput is not null)
            {
                _saveInput.IsChecked = value;
            }
            else
            {
                _saveToUserSecrets = value;
            }
        }
    }

    private bool IsSubmitDisabled =>
        Editors.Count == 0 ||
        Editors.Any(editor => IsEditorDisabled(editor) || IsMissingRequiredValue(editor.ValueInput));

    // The AppHost's inputs interaction, when it's shown in this panel. The interaction is submitted as a whole, so the
    // panel uses it only when it shows every parameter the interaction asks for. Otherwise each parameter is set with
    // its own command.
    private InteractionsInputsDialogViewModel? Interaction =>
        ParameterInteractionState.Current is { } current && _registration?.Shows(current) == true ? current : null;

    protected override void OnInitialized()
    {
        MarkdownProcessor = InteractionMarkdownHelper.CreateProcessor(ControlsStringsLoc);
        _registration = ParameterInteractionState.RegisterPanel();
        ParameterInteractionState.Changed += OnParameterInteractionChanged;
    }

    protected override async Task OnParametersSetAsync()
    {
        // The registered parameters decide whether the interaction is shown here, so update them before the editors.
        _registration!.SetParameterNames(Parameters
            .Where(parameter => GetSetParameterCommand(parameter) is not null)
            .Select(parameter => parameter.Name));
        SyncEditors();

        await ParameterInteractionState.BeginInteractionAsync(_registration);
    }

    private static CommandViewModel? GetSetParameterCommand(ResourceViewModel parameter) =>
        parameter.Commands.FirstOrDefault(command =>
            string.Equals(command.Name, CommandViewModel.SetParameterCommand, StringComparisons.CommandName) &&
            command.State != CommandViewModelState.Hidden);

    private void SyncEditors()
    {
        var existingEditors = _editors.ToDictionary(editor => editor.Resource.Name, StringComparers.ResourceName);
        var editors = new List<ParameterEditor>();
        var interaction = Interaction;
        var interactionInputs = interaction?.Inputs
            .ToDictionary(input => input.Name, StringComparers.InteractionInputName);

        foreach (var parameter in Parameters
            .Where(parameter => parameter.HasMissingParameterValueState())
            .OrderBy(parameter => parameter.DisplayName, StringComparers.ResourceName))
        {
            var command = GetSetParameterCommand(parameter);
            var valueInput = interactionInputs?.GetValueOrDefault(parameter.Name) ??
                command?.ArgumentInputs.FirstOrDefault(input =>
                    !string.Equals(input.Name, SaveToUserSecretsName, StringComparisons.InteractionInputName));
            if (command is null || valueInput is null)
            {
                continue;
            }

            var saveInput = command.ArgumentInputs.FirstOrDefault(input =>
                string.Equals(input.Name, SaveToUserSecretsName, StringComparisons.InteractionInputName));

            if (existingEditors.TryGetValue(parameter.Name, out var existingEditor))
            {
                existingEditor.ValueInput.SetInput(valueInput);
                editors.Add(existingEditor with { Resource = parameter, Command = command, SaveInput = saveInput });
            }
            else
            {
                editors.Add(new ParameterEditor(parameter, command, new InputViewModel(valueInput), saveInput));
            }
        }

        _editors = editors;

        var interactionSaveInput = interaction?.Inputs.FirstOrDefault(input =>
            string.Equals(input.Name, SaveToUserSecretsName, StringComparisons.InteractionInputName));
        if (interactionSaveInput is not null)
        {
            _saveInput ??= new InputViewModel(interactionSaveInput);
            _saveInput.SetInput(interactionSaveInput);
        }
        else
        {
            // The interaction completed, or moved to a dialog.
            _saveInput = null;
        }
    }

    private bool IsEditorDisabled(ParameterEditor editor) =>
        editor.ValueInput.InputDisabled ||
        editor.Command.State == CommandViewModelState.Disabled ||
        IsCommandExecuting(editor.Resource, editor.Command);

    private static bool IsMissingRequiredValue(InputViewModel input) =>
        input.Input.Required &&
        input.Input.InputType != InputType.Boolean &&
        string.IsNullOrWhiteSpace(input.Value);

    private async Task SubmitAsync()
    {
        if (IsSubmitDisabled)
        {
            return;
        }

        if (Interaction is { } interaction)
        {
            await interaction.OnSubmitCallback(interaction.Interaction, false);
            return;
        }

        foreach (var editor in Editors.Where(editor => editor.ValueInput.Input.InputType != InputType.File))
        {
            var arguments = new Dictionary<string, Value>
            {
                [editor.ValueInput.Input.Name] = GetArgumentValue(editor.ValueInput)
            };
            if (editor.SaveInput is { } saveInput)
            {
                arguments[saveInput.Name] = Value.ForBool(SaveToUserSecrets);
            }

            await OnExecuteCommandWithOptionsAsync(editor.Resource, editor.Command, new ExecuteResourceCommandOptions
            {
                Arguments = arguments,
                NonInteractive = true
            });
        }
    }

    private static long GetMaxFileSize(InputViewModel input) =>
        input.Input.MaxFileSize > 0 ? input.Input.MaxFileSize : InputViewModel.DefaultMaxUploadedFileBytes;

    private async Task OnInputFileChangeAsync(ParameterEditor editor, InputFileChangeEventArgs args)
    {
        if (Interaction is not { } interaction)
        {
            return;
        }

        var input = editor.ValueInput;
        var maxFileSize = GetMaxFileSize(input);
        var fileReferences = new List<FileReferenceViewModel>();
        foreach (var file in args.GetMultipleFiles(InteractionHelpers.MaxFileCount))
        {
            if (file.Size > maxFileSize)
            {
                fileReferences.Add(new FileReferenceViewModel
                {
                    Name = file.Name,
                    ErrorMessage = string.Format(
                        CultureInfo.CurrentCulture,
                        DialogsLoc[nameof(Resources.Dialogs.InteractionFileExceedsMaxSize)],
                        FormatHelpers.FormatFileSize(maxFileSize))
                });
                continue;
            }

            try
            {
                using var stream = file.OpenReadStream(maxFileSize);
                var fileId = await interaction.DashboardClient.UploadFileAsync(
                    stream,
                    file.Name,
                    file.Size,
                    interaction.Interaction.InteractionId,
                    input.Input.Name,
                    _disposalCts.Token);
                fileReferences.Add(new FileReferenceViewModel { Id = fileId, Name = file.Name });
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                fileReferences.Add(new FileReferenceViewModel
                {
                    Name = file.Name,
                    ErrorMessage = DialogsLoc[nameof(Resources.Dialogs.InteractionFileUploadFailed)]
                });
            }
        }

        input.SetFileReferences(fileReferences);
    }

    private static Value GetArgumentValue(InputViewModel input) =>
        input.Input.InputType switch
        {
            InputType.Boolean => Value.ForBool(input.IsChecked),
            InputType.Number when input.NumberValue is { } number => Value.ForNumber(number),
            _ => Value.ForString(input.Value)
        };

    private static void OnChoiceTextChanged(InputViewModel input, string? text)
    {
        if (input.SelectedOption?.Name == text)
        {
            return;
        }

        text ??= string.Empty;
        input.SelectedOption = input.SelectOptions.FirstOrDefault(option => option.Name == text)
            ?? new SelectViewModel<string> { Id = text, Name = text };
        UpdateChoiceValueState(input);
    }

    private static void UpdateChoiceValueState(InputViewModel input)
    {
        input.SelectedOption ??= input.SelectOptions.FirstOrDefault(option => option.Id == input.Value)
            ?? new SelectViewModel<string> { Id = input.Value, Name = input.Value };
        input.Value = input.SelectedOption.Id;
    }

    private static string GetFileButtonText(ParameterEditor editor) =>
        string.IsNullOrEmpty(editor.ValueInput.Input.Placeholder)
            ? editor.Command.GetDisplayName()
            : editor.ValueInput.Input.Placeholder;

    private void OnParameterInteractionChanged()
    {
        _ = InvokeAsync(async () =>
        {
            SyncEditors();
            StateHasChanged();
            await ParameterInteractionState.BeginInteractionAsync(_registration!);
        });
    }

    public ValueTask DisposeAsync()
    {
        ParameterInteractionState.Changed -= OnParameterInteractionChanged;
        _registration?.Dispose();
        _disposalCts.Cancel();
        _disposalCts.Dispose();
        return ValueTask.CompletedTask;
    }

    private sealed record ParameterEditor(
        ResourceViewModel Resource,
        CommandViewModel Command,
        InputViewModel ValueInput,
        InteractionInput? SaveInput);
}
