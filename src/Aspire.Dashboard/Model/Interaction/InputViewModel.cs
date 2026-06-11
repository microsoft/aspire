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
        // Interaction updates carry a full server-side snapshot even when only one input changed. Keep
        // local values by default so an update for a dependent choice does not clobber text the user is
        // typing elsewhere in the dialog. ShouldUseIncomingValue captures the cases where the server is
        // authoritative because the field is being dynamically loaded or is not currently editable.
        var value = Input is null || ShouldUseIncomingValue(Input, input)
            ? input.Value
            : Input.Value;
        input.Value = value;

        Input = input;
        if (input.InputType == InputType.Choice && input.Options != null)
        {
            var optionsVM = input.Options
                .Select(option => new SelectViewModel<string> { Id = option.Key, Name = option.Value, })
                .ToList();

            // Only update the options if they have changed to avoid unnecessarily recreating the FluentSelect component.
            if (!OptionsEqual(SelectOptions, optionsVM))
            {
                SelectOptions = optionsVM;
                ChoiceVersion++;
            }

            // Default to the first option if no placeholder is set, the value is empty, and custom choice is disabled.
            // This is done so the input model value matches frontend behavior (FluentSelect defaults to the first option)
            if (string.IsNullOrEmpty(input.Placeholder) && string.IsNullOrEmpty(input.Value) && optionsVM.Count > 0 && !input.AllowCustomChoice)
            {
                input.Value = optionsVM[0].Id;
            }
        }
    }

    public List<SelectViewModel<string>> SelectOptions { get; private set; } = [];

    /// <summary>
    /// Incremented each time <see cref="SelectOptions"/> is rebuilt so Blazor
    /// recreates the choice input component when options change, avoiding a race
    /// where the web component clears the bound value during an options refresh.
    /// </summary>
    public int ChoiceVersion { get; private set; }

    /// <summary>
    /// A key unique per input that changes when <see cref="ChoiceVersion"/> changes.
    /// Used as a Blazor <c>@key</c> on FluentSelect / FluentCombobox.
    /// </summary>
    public string InputKey => $"{Input.Name}_{ChoiceVersion}";

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

    private static bool OptionsEqual(List<SelectViewModel<string>> existing, List<SelectViewModel<string>> incoming)
    {
        if (existing.Count != incoming.Count)
        {
            return false;
        }

        for (var i = 0; i < existing.Count; i++)
        {
            if (existing[i].Id != incoming[i].Id || existing[i].Name != incoming[i].Name)
            {
                return false;
            }
        }

        return true;
    }

    private static bool ShouldUseIncomingValue(InteractionInput current, InteractionInput incoming)
    {
        // Dynamic loading can replace both the option list and the selected value. When loading
        // completes, the server value is the one validated against the freshly loaded options.
        //
        // Disabled inputs are also server-owned because the user could not have made a meaningful local
        // edit while the control was unavailable. This includes disabled -> enabled transitions, such as
        // Azure Subscription ID becoming editable after tenant-specific subscriptions are loaded.
        return (current.Loading && !incoming.Loading) || current.Disabled || incoming.Disabled;
    }
}
