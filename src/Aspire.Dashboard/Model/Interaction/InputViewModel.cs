// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using Aspire.DashboardService.Proto.V1;

namespace Aspire.Dashboard.Model.Interaction;

public sealed class InputViewModel
{
    public InteractionInput Input { get; private set; } = default!;

    public InputViewModel(InteractionInput input)
    {
        SetInput(input);
    }

    public void SetInput(InteractionInput input)
    {
        string value;
        if (Input == null)
        {
            value = input.Value;
        }
        else
        {
            // Only overwrite the local value if the input was loading and is no longer loading (update could have come from server)
            // This avoids changes in local values being overwritten by a dynamic server update.
            if (Input.Loading && !input.Loading)
            {
                value = input.Value;
            }
            else
            {
                value = Input.Value;
            }
        }
        input.Value = value;

        Input = input;
        if (input.InputType == InputType.Choice && input.Options != null)
        {
            var optionsVM = input.Options
                .Select(option => new SelectViewModel<string> { Id = option.Key, Name = option.Value, })
                .ToList();

            SelectOptions = optionsVM;

            // Default to the first option if no placeholder is set, the value is empty, and custom choice is disabled.
            // This is done so the input model value matches frontend behavior (FluentSelect defaults to the first option)
            if (string.IsNullOrEmpty(input.Placeholder) && string.IsNullOrEmpty(input.Value) && optionsVM.Count > 0 && !input.AllowCustomChoice)
            {
                input.Value = optionsVM[0].Id;
            }
        }
    }

    public List<SelectViewModel<string>> SelectOptions { get; private set; } = [];

    public IEnumerable<SelectViewModel<string>> FilteredOptions()
    {
        if (Value is not { Length: > 0 } value)
        {
            return SelectOptions;
        }

        var filteredValues = SelectOptions.Where(vm => vm.Name.Contains(value, StringComparison.OrdinalIgnoreCase));

        // If no values match the filter, don't apply the filter.
        // This improves user experience and fixes some combobox issues.
        // https://github.com/microsoft/fluentui-blazor/issues/4314#issuecomment-3577475233
        if (!filteredValues.Any())
        {
            filteredValues = SelectOptions;
        }

        return filteredValues;
    }

    public string? Value
    {
        get => Input.Value;
        set => Input.Value = value;
    }

    // Used when binding to FluentCheckbox.
    public bool IsChecked
    {
        get => bool.TryParse(Input.Value, out var result) && result;
        set => Input.Value = value ? "true" : "false";
    }

    // Used when binding to FluentNumberField.
    public int? NumberValue
    {
        get => int.TryParse(Input.Value, CultureInfo.InvariantCulture, out var result) ? result : null;
        set => Input.Value = value?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
    }

    public bool InputDisabled => Input.Disabled || Input.Loading;

    // Used to track secret text visibility state
    public bool IsSecretTextVisible { get; set; }

    // Used to display the original filename for FileChooser inputs while Value holds the file contents.
    public string? FileDisplayName { get; set; }
}
