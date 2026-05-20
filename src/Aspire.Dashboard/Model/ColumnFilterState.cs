// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Concurrent;

namespace Aspire.Dashboard.Model;

/// <summary>
/// Tracks filter state for a single grid column — which discrete values are visible.
/// </summary>
internal sealed class ColumnFilterState
{
    /// <summary>
    /// Maps each known value to whether it is currently visible (checked).
    /// All values default to visible (true) when first added.
    /// </summary>
    public ConcurrentDictionary<string, bool> Values { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Optional free-text filter applied within this column (for substring matching).
    /// </summary>
    public string FreeText { get; set; } = string.Empty;

    /// <summary>
    /// Returns true if no filtering is active (all values visible and no free text).
    /// </summary>
    public bool IsUnfiltered => FreeText.Length == 0 && (Values.IsEmpty || Values.Values.All(v => v));

    /// <summary>
    /// Updates the known value set from the current data source.
    /// New values default to visible. Stale values are removed.
    /// </summary>
    public void UpdateAvailableValues(IEnumerable<string> currentValues)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var value in currentValues)
        {
            seen.Add(value);
            Values.TryAdd(value, true);
        }

        // Remove values that no longer exist in the data
        foreach (var key in Values.Keys)
        {
            if (!seen.Contains(key))
            {
                Values.TryRemove(key, out _);
            }
        }
    }

    /// <summary>
    /// Sets all values to the specified visibility state.
    /// </summary>
    public void SetAll(bool visible)
    {
        foreach (var key in Values.Keys)
        {
            Values[key] = visible;
        }
    }

    /// <summary>
    /// Returns true if the given value passes this column's filter.
    /// A value passes if it is in the visible set (or the set is empty) and matches the free text.
    /// </summary>
    public bool IsMatch(string? value)
    {
        if (value is null)
        {
            return true;
        }

        // Check discrete value filter
        if (!Values.IsEmpty && Values.TryGetValue(value, out var isVisible) && !isVisible)
        {
            return false;
        }

        // Check free-text filter
        if (FreeText.Length > 0 && !value.Contains(FreeText, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return true;
    }
}
