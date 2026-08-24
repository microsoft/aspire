// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Immutable;
using System.Diagnostics;
using Aspire.Dashboard.Extensions;

namespace Aspire.Dashboard.Model;

[DebuggerDisplay("{DebuggerToString(),nq}")]
public class DimensionFilterViewModel
{
    private string? _sanitizedHtmlId;
    private ImmutableHashSet<DimensionValueViewModel> _selectedValues = [];

    public required string Name { get; init; }
    public List<DimensionValueViewModel> Values { get; } = [];
    public IReadOnlySet<DimensionValueViewModel> SelectedValues => Volatile.Read(ref _selectedValues);
    public bool PopupVisible { get; set; }

    /// <summary>
    /// Invoked when the filter state is modified externally (e.g., from the popover)
    /// so that subscribed components can re-render.
    /// </summary>
    public Action? NotifyStateChanged { get; set; }

    public bool? AreAllValuesSelected
    {
        get
        {
            var selectedValues = SelectedValues;
            return selectedValues.SetEquals(Values)
                ? true
                : selectedValues.Count == 0
                    ? false
                    : null;
        }
        set
        {
            if (value is true)
            {
                Interlocked.Exchange(ref _selectedValues, Values.ToImmutableHashSet());
            }
            else if (value is false)
            {
                // Only clear if all values are currently selected.
                // FluentCheckbox's three-state handling can spuriously fire the setter with false
                // when the state transitions from true to null (intermediate) due to individual
                // checkbox changes. In that case, AreAllValuesSelected is already null/false,
                // and we should not clear the remaining selections.
                var allValues = Values.ToImmutableHashSet();
                ImmutableInterlocked.Update(
                    ref _selectedValues,
                    static (selectedValues, allValues) => selectedValues.SetEquals(allValues) ? [] : selectedValues,
                    allValues);
            }
            // When value is null (intermediate state), do nothing.
        }
    }

    public string SanitizedHtmlId => _sanitizedHtmlId ??= StringExtensions.SanitizeHtmlId(Name);

    public void SetSelectedValues(IEnumerable<DimensionValueViewModel> dimensionValues)
    {
        Interlocked.Exchange(ref _selectedValues, dimensionValues.ToImmutableHashSet());
    }

    public void OnTagSelectionChanged(DimensionValueViewModel dimensionValue, bool isChecked)
    {
        ImmutableInterlocked.Update(
            ref _selectedValues,
            static (selectedValues, state) => state.IsChecked
                ? selectedValues.Add(state.DimensionValue)
                : selectedValues.Remove(state.DimensionValue),
            (DimensionValue: dimensionValue, IsChecked: isChecked));
    }

    private string DebuggerToString() => $"Name = {Name}, SelectedValues = {SelectedValues.Count}";
}

[DebuggerDisplay("Text = {Text}, Value = {Value}")]
public class DimensionValueViewModel
{
    public required string Text { get; init; }
    public required string? Value { get; init; }
}
