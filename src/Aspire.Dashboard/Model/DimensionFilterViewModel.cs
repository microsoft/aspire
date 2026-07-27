// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using Aspire.Dashboard.Extensions;

namespace Aspire.Dashboard.Model;

[DebuggerDisplay("{DebuggerToString(),nq}")]
public class DimensionFilterViewModel
{
    private string? _sanitizedHtmlId;

    public required string Name { get; init; }
    public List<DimensionValueViewModel> Values { get; } = new();
    public HashSet<DimensionValueViewModel> SelectedValues { get; } = new();
    public bool PopupVisible { get; set; }

    public bool? AreAllValuesSelected
    {
        get
        {
            return SelectedValues.SetEquals(Values)
                ? true
                : SelectedValues.Count == 0
                    ? false
                    : null;
        }
        set
        {
            if (value is true)
            {
                SelectedValues.UnionWith(Values);
            }
            else if (value is false)
            {
                // Only clear if all values are currently selected.
                // FluentCheckbox's three-state handling can spuriously fire the setter with false
                // when the state transitions from true to null (intermediate) due to individual
                // checkbox changes. In that case, AreAllValuesSelected is already null/false,
                // and we should not clear the remaining selections.
                if (AreAllValuesSelected is true)
                {
                    SelectedValues.Clear();
                }
            }
            // When value is null (intermediate state), do nothing.
        }
    }

    public string SanitizedHtmlId => _sanitizedHtmlId ??= StringExtensions.SanitizeHtmlId(Name);

    public void OnTagSelectionChanged(DimensionValueViewModel dimensionValue, bool isChecked)
    {
        if (isChecked)
        {
            SelectedValues.Add(dimensionValue);
        }
        else
        {
            SelectedValues.Remove(dimensionValue);
        }
    }

    private string DebuggerToString() => $"Name = {Name}, SelectedValues = {SelectedValues.Count}";
}

[DebuggerDisplay("Text = {Text}, Value = {Value}")]
public class DimensionValueViewModel
{
    public required string Text { get; init; }
    public required string? Value { get; init; }
}

