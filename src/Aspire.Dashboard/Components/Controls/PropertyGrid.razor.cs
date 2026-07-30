// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Components.Controls.Grid;
using Aspire.Dashboard.Model;
using Aspire.Dashboard.Resources;
using Microsoft.AspNetCore.Components;
using System.Globalization;

namespace Aspire.Dashboard.Components.Controls;

/// <summary>
/// Describes an name/value item to be displayed in a <see cref="PropertyGrid{TItem}"/>.
/// </summary>
/// <remarks>
/// <para>
/// Implement this interface to use as the <c>TItem</c> of a <see cref="PropertyGrid{TItem}"/> component.
/// </para>
/// <para>
/// The property grid has two columns, bound to display strings <see cref="Name"/> and <see cref="Value"/>.
/// </para>
/// <para>
/// The <see cref="IsValueSensitive"/> and <see cref="IsValueMasked"/> properties control masking behavior,
/// which prevents sensitive data from being displayed in the UI without user interaction.
/// </para>
/// </remarks>
public interface IPropertyGridItem
{
    /// <summary>
    /// Gets the display name of the item.
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Gets the key of the item. Must be unique.
    /// </summary>
    public object Key => Name;

    /// <summary>
    /// Gets the display value of the item.
    /// </summary>
    string? Value { get; }

    /// <summary>
    /// Overrides the value to visualize. If <see langword="null"/>, <see cref="Value"/> is visualized.
    /// </summary>
    public string? ValueToVisualize => null;

    /// <summary>
    /// Gets whether this item's value is sensitive and should be masked.
    /// </summary>
    /// <remarks>
    /// Default implementation returns <see langword="false"/>.
    /// </remarks>
    public bool IsValueSensitive => false;

    /// <summary>
    /// Gets and sets whether this item's value is masked in the UI by default.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Masking is a security and privacy feature that causes values to appear as asterisks or other
    /// characters in the UI. This is useful for sensitive data like passwords or API keys.
    /// The user may choose to reveal the value by toggling the mask.
    /// </para>
    /// <para>
    /// Only used when <see cref="IsValueSensitive"/> is <see langword="true"/>. Otherwise this property
    /// is ignored.
    /// </para>
    /// </remarks>
    public bool IsValueMasked { get => false; set => throw new NotImplementedException(); }

    /// <summary>
    /// Gets whether this item matches a filter string.
    /// </summary>
    /// <remarks>
    /// Default implementation checks against <see cref="Name"/> and <see cref="Value"/>.
    /// </remarks>
    /// <param name="filter">The search text to match against.</param>
    /// <returns><see langword="true"/> if this item matches the filter, otherwise <see langword="false"/>.</returns>
    public bool MatchesFilter(string filter)
        => Name?.Contains(filter, StringComparison.CurrentCultureIgnoreCase) == true ||
           Value?.Contains(filter, StringComparison.CurrentCultureIgnoreCase) == true;
}

public partial class PropertyGrid<TItem> where TItem : IPropertyGridItem
{
    private static readonly RenderFragment<TItem> s_emptyChildContent = _ => builder => { };

    // Default sort key selectors. The value column sorts on the visible text, treating masked
    // values as null so hidden secrets don't leak their ordering.
    private static readonly Func<TItem, IComparable?> s_defaultNameSort = static vm => vm.Name;
    private static readonly Func<TItem, IComparable?> s_defaultValueSort = static vm => vm.IsValueMasked ? null : vm.Value;

    // Sort state. Column 0 = name, 1 = value, null = unsorted (items shown in source order).
    private int? _sortColumnIndex;
    private bool _sortAscending = true;

    [Parameter, EditorRequired]
    public IQueryable<TItem>? Items { get; set; }

    [Parameter]
    public Func<TItem, object?> ItemKey { get; init; } = static item => item.Key;

    [Parameter]
    public string GridTemplateColumns { get; set; } = "1fr 1fr";

    [Parameter]
    public string? NameColumnTitle { get; set; }

    [Parameter]
    public string? ValueColumnTitle { get; set; }

    [Parameter]
    public bool Multiline { get; set; }

    /// <summary>
    /// Gets and sets the sorting behavior of the name column. Defaults to sorting on <see cref="IPropertyGridItem.Name"/>.
    /// </summary>
    [Parameter]
    public Func<TItem, IComparable?> NameSort { get; set; } = s_defaultNameSort;

    /// <summary>
    /// Gets and sets the sorting behavior of the value column. Defaults to sorting on <see cref="IPropertyGridItem.Value"/>.
    /// </summary>
    [Parameter]
    public Func<TItem, IComparable?> ValueSort { get; set; } = s_defaultValueSort;

    [Parameter]
    public bool IsNameSortable { get; set; } = true;

    [Parameter]
    public bool IsValueSortable { get; set; } = true;

    [Parameter]
    public RenderFragment<TItem> ContentAfterValue { get; set; } = s_emptyChildContent;

    [Parameter]
    public string? HighlightText { get; set; }

    [Parameter]
    public EventCallback<TItem> IsValueMaskedChanged { get; set; }

    [Parameter]
    public RenderFragment<TItem> ExtraValueContent { get; set; } = s_emptyChildContent;

    [Parameter]
    public GridHeaderMode GenerateHeader { get; set; } = GridHeaderMode.Default;

    [Parameter]
    public string? Class { get; set; }

    [Parameter]
    public Dictionary<string, ComponentMetadata>? ValueComponents { get; set; }

    private string NameColumnTitleText => NameColumnTitle ?? Loc[nameof(ControlsStrings.NameColumnHeader)];
    private string ValueColumnTitleText => ValueColumnTitle ?? Loc[nameof(ControlsStrings.PropertyGridValueColumnHeader)];

    private string TableCssClass
    {
        get
        {
            var css = "data property-grid-table";
            if (Multiline)
            {
                css += " multiline";
            }
            if (!string.IsNullOrEmpty(Class))
            {
                css += " " + Class;
            }
            return css;
        }
    }

    // The property grid always has exactly two columns (name/value). Translate the fractional
    // grid-template-columns string (e.g. "1fr 2fr") that callers pass into percentage widths for
    // the native table's <col> elements. Non-fractional tokens (e.g. "150px") are used verbatim.
    private (string Name, string Value) GetColumnWidths()
    {
        var tokens = GridTemplateColumns.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 2 &&
            TryParseFraction(tokens[0], out var first) &&
            TryParseFraction(tokens[1], out var second) &&
            first + second > 0)
        {
            var total = first + second;
            return (FormatPercent(first / total), FormatPercent(second / total));
        }

        return (tokens.ElementAtOrDefault(0) ?? "auto", tokens.ElementAtOrDefault(1) ?? "auto");

        static string FormatPercent(double fraction) =>
            (fraction * 100).ToString("0.###", CultureInfo.InvariantCulture) + "%";
    }

    private static bool TryParseFraction(string token, out double value)
    {
        if (token.EndsWith("fr", StringComparison.OrdinalIgnoreCase) &&
            double.TryParse(token[..^2], NumberStyles.Float, CultureInfo.InvariantCulture, out value))
        {
            return true;
        }

        value = 0;
        return false;
    }

    private IEnumerable<TItem> GetSortedItems()
    {
        var items = Items ?? Enumerable.Empty<TItem>().AsQueryable();

        return _sortColumnIndex switch
        {
            0 => _sortAscending ? items.OrderBy(NameSort) : items.OrderByDescending(NameSort),
            1 => _sortAscending ? items.OrderBy(ValueSort) : items.OrderByDescending(ValueSort),
            _ => items
        };
    }

    // Clicking a sortable header cycles it into ascending, then toggles ascending/descending.
    private void OnHeaderClicked(int columnIndex)
    {
        if (_sortColumnIndex == columnIndex)
        {
            _sortAscending = !_sortAscending;
        }
        else
        {
            _sortColumnIndex = columnIndex;
            _sortAscending = true;
        }
    }

    // aria-sort value for the header cell, matching WAI-ARIA grid semantics.
    private string GetAriaSort(int columnIndex)
    {
        if (_sortColumnIndex != columnIndex)
        {
            return "none";
        }

        return _sortAscending ? "ascending" : "descending";
    }

    // Return null if empty so GridValue knows there is no template.
    private RenderFragment? GetContentAfterValue(TItem context) => ContentAfterValue == s_emptyChildContent
        ? null
        : ContentAfterValue(context);

    private async Task OnIsValueMaskedChanged(TItem item, bool isValueMasked)
    {
        item.IsValueMasked = isValueMasked;

        await IsValueMaskedChanged.InvokeAsync(item);
    }

    private ComponentMetadata? GetComponentMetadata(TItem item)
    {
        if (ValueComponents is null)
        {
            return null;
        }
        ValueComponents.TryGetValue(item.Key as string ?? item.Name, out var metadata);
        return metadata;
    }
}
