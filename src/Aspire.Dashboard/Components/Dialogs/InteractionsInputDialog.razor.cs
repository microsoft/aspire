// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using Aspire.Dashboard.Components.Deck;
using Aspire.Dashboard.Model.Interaction;
using Aspire.Dashboard.Model.Markdown;
using Aspire.Dashboard.Resources;
using Aspire.Dashboard.Utils;
using Aspire.DashboardService.Proto.V1;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.Localization;
using Microsoft.FluentUI.AspNetCore.Components;
using Microsoft.JSInterop;

namespace Aspire.Dashboard.Components.Dialogs;

public partial class InteractionsInputDialog : IAsyncDisposable
{
    [Parameter]
    public InteractionsInputsDialogViewModel Content { get; set; } = default!;

    [CascadingParameter]
    public FluentDialog Dialog { get; set; } = default!;

    [Inject]
    public required IStringLocalizer<ControlsStrings> ControlsStringsLoc { get; init; }

    [Inject]
    public required IStringLocalizer<Resources.Dialogs> Loc { get; init; }

    [Inject]
    public required IJSRuntime JS { get; init; }

    private InteractionsInputsDialogViewModel? _content;
    private EditContext _editContext = default!;
    private ValidationMessageStore _validationMessages = default!;
    private List<InputViewModel> _inputDialogInputViewModels = default!;

    // Stable DOM id per input field. The native <input>/<select> controls (and the FluentCombobox)
    // need an explicit id so the <label for> association, the secret-text type toggle, and the
    // initial-focus logic can address the correct element from C#/JS. The id is generated once per
    // input and kept stable across renders so focus/validation targets don't change underneath us.
    private Dictionary<InputViewModel, string> _fieldIds = default!;
    private readonly string _fieldIdPrefix = $"interaction-input-{Guid.NewGuid():N}";

    private MarkdownProcessor _markdownProcessor = default!;
    private IJSObjectReference? _jsModule;

    protected override void OnInitialized()
    {
        _editContext = new EditContext(Content);
        _validationMessages = new ValidationMessageStore(_editContext);

        _editContext.OnValidationRequested += (s, e) => ValidateModel();
        _editContext.OnFieldChanged += (s, e) => InputValueChanged(e.FieldIdentifier);

        _fieldIds = new();
        _markdownProcessor = InteractionMarkdownHelper.CreateProcessor(ControlsStringsLoc);
    }

    protected override void OnParametersSet()
    {
        if (_content != Content)
        {
            _content = Content;
            _inputDialogInputViewModels = Content.Inputs.Select(input => new InputViewModel(input)).ToList();

            // Assign a stable DOM id to each input so the label/secret-toggle/focus logic can address it.
            _fieldIds.Clear();
            for (var i = 0; i < _inputDialogInputViewModels.Count; i++)
            {
                _fieldIds[_inputDialogInputViewModels[i]] = $"{_fieldIdPrefix}-{i}";
            }

            AddValidationErrorsFromModel();

            Content.OnInteractionUpdated = async () =>
            {
                AddValidationErrorsFromModel();

                await InvokeAsync(StateHasChanged);
            };
        }
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender)
        {
            _jsModule = await JS.InvokeAsync<IJSObjectReference>("import", "./Components/Dialogs/InteractionsInputDialog.razor.js");

            // Focus the first input when the dialog loads. Focus is driven from JS by element id
            // because the inputs are a mix of native controls and a web component (combobox).
            if (_inputDialogInputViewModels.Count > 0 && _fieldIds.TryGetValue(_inputDialogInputViewModels[0], out var firstInputId))
            {
                await _jsModule.InvokeVoidAsync("focusElement", firstInputId);
            }
        }
    }

    private void AddValidationErrorsFromModel()
    {
        for (var i = 0; i < Content.Inputs.Count; i++)
        {
            var inputModel = Content.Inputs[i];
            var inputViewModel = _inputDialogInputViewModels[i];

            inputViewModel.SetInput(inputModel);

            var field = GetFieldIdentifier(inputViewModel);
            foreach (var validationError in inputModel.ValidationErrors)
            {
                _validationMessages.Add(field, validationError);
            }
        }
    }

    private void ValidateModel()
    {
        _validationMessages.Clear();

        foreach (var inputModel in _inputDialogInputViewModels)
        {
            var field = GetFieldIdentifier(inputModel);
            if (IsMissingRequiredValue(inputModel))
            {
                _validationMessages.Add(field, $"{inputModel.Input.Label} is required.");
            }
        }

        _editContext.NotifyValidationStateChanged();
    }

    private void InputValueChanged(FieldIdentifier field)
    {
        _validationMessages.Clear(field);

        if (field.Model is InputViewModel inputModel)
        {
            if (IsMissingRequiredValue(inputModel))
            {
                _validationMessages.Add(field, $"{inputModel.Input.Label} is required.");
            }

            if (inputModel.Input.UpdateStateOnChange)
            {
                _ = Content.OnSubmitCallback(Content.Interaction, true);
            }
        }

        _editContext.NotifyValidationStateChanged();
    }

    // The native <input>/<select> controls don't integrate with EditContext the way the previous
    // Fluent input components did, so the change handlers below update the bound value and explicitly
    // notify EditContext. That keeps live validation and InteractionInput.UpdateStateOnChange working.
    private void OnStringValueChanged(InputViewModel inputModel, ChangeEventArgs e)
    {
        inputModel.Value = e.Value?.ToString();
        _editContext.NotifyFieldChanged(GetFieldIdentifier(inputModel));
    }

    private void OnNumberValueChanged(InputViewModel inputModel, ChangeEventArgs e)
    {
        var text = e.Value?.ToString();
        inputModel.NumberValue = int.TryParse(text, CultureInfo.InvariantCulture, out var result) ? result : null;
        _editContext.NotifyFieldChanged(GetFieldIdentifier(inputModel));
    }

    private void OnCheckedChanged(InputViewModel inputModel, bool isChecked)
    {
        inputModel.IsChecked = isChecked;
        _editContext.NotifyFieldChanged(GetFieldIdentifier(inputModel));
    }

    private static FieldIdentifier GetFieldIdentifier(InputViewModel inputModel)
    {
        var fieldName = inputModel.Input.InputType switch
        {
            InputType.Boolean => nameof(inputModel.IsChecked),
            InputType.Number => nameof(inputModel.NumberValue),
            _ => nameof(inputModel.Value)
        };
        return new FieldIdentifier(inputModel, fieldName);
    }

    private static bool IsMissingRequiredValue(InputViewModel inputModel)
    {
        return inputModel.Input.Required &&
            inputModel.Input.InputType != InputType.Boolean &&
            string.IsNullOrWhiteSpace(inputModel.Value);
    }

    private async Task SubmitAsync()
    {
        // The workflow is:
        // 1. Validate the model that required fields are present.
        // 2. Run submit callback. Sends input values to the server.
        // 3. If validation on the server passes, a completion dialog is send back to the client which closes the dialog.
        // 4. If validation fails, the server sends back validation errors which are displayed in the dialog.
        if (_editContext.Validate())
        {
            await Content.OnSubmitCallback(Content.Interaction, false);
        }
    }

    private async Task CancelAsync()
    {
        await Dialog.CancelAsync();
    }

    private async Task ToggleSecretTextVisibilityAsync(InputViewModel inputModel)
    {
        inputModel.IsSecretTextVisible = !inputModel.IsSecretTextVisible;

        if (_jsModule != null && _fieldIds.TryGetValue(inputModel, out var elementId))
        {
            await _jsModule.InvokeVoidAsync("togglePasswordVisibility", elementId);
        }
    }

    private static DeckIconName GetSecretTextIcon(InputViewModel inputModel)
    {
        return inputModel.IsSecretTextVisible
            ? DeckIconName.EyeOff
            : DeckIconName.Eye;
    }

    public async ValueTask DisposeAsync()
    {
        await JSInteropHelpers.SafeDisposeAsync(_jsModule);
    }
}
