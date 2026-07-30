// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Dashboard.Components.Controls.Grid;

/// <summary>
/// Tracks the active sort column and direction for a Deck data table and applies the corresponding
/// ordering. Replaces the the previous library data grid's built-in column sorting for bespoke native tables.
/// </summary>
/// <typeparam name="TItem">The row item type.</typeparam>
public sealed class GridSortState<TItem>
{
    /// <summary>
    /// Gets the index of the currently sorted column, or <see langword="null"/> when the table is
    /// unsorted and rows are shown in source order.
    /// </summary>
    public int? ColumnIndex { get; private set; }

    /// <summary>
    /// Gets whether the active column is sorted ascending.
    /// </summary>
    public bool Ascending { get; private set; } = true;

    /// <summary>
    /// Toggles sorting for the given column. Selecting a new column sorts ascending; selecting the
    /// active column flips the direction.
    /// </summary>
    public void Toggle(int columnIndex)
    {
        if (ColumnIndex == columnIndex)
        {
            Ascending = !Ascending;
        }
        else
        {
            ColumnIndex = columnIndex;
            Ascending = true;
        }
    }

    /// <summary>
    /// Gets whether the given column is the active sort column.
    /// </summary>
    public bool IsSorted(int columnIndex) => ColumnIndex == columnIndex;

    /// <summary>
    /// Orders <paramref name="items"/> using the key selector for the active column. When no column
    /// is active, or the active index has no selector, the source order is returned unchanged.
    /// </summary>
    public IEnumerable<TItem> Apply(IEnumerable<TItem> items, IReadOnlyList<Func<TItem, IComparable?>> keySelectors)
    {
        if (ColumnIndex is not int index || index < 0 || index >= keySelectors.Count)
        {
            return items;
        }

        var keySelector = keySelectors[index];
        return Ascending ? items.OrderBy(keySelector) : items.OrderByDescending(keySelector);
    }
}
