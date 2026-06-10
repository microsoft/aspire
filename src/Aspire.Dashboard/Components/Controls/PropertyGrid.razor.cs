// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Model;
using Aspire.Dashboard.Utils;
using Microsoft.AspNetCore.Components;
using Microsoft.FluentUI.AspNetCore.Components;

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

    private static readonly GridSort<TItem> s_defaultNameSort = GridSort<TItem>.ByAscending(vm => vm.Name);
    private static readonly GridSort<TItem> s_defaultValueSort = GridSort<TItem>.ByAscending(vm => vm.IsValueMasked ? null : vm.Value);

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
    public GridSort<TItem> NameSort { get; set; } = s_defaultNameSort;

    /// <summary>
    /// Gets and sets the sorting behavior of the value column. Defaults to sorting on <see cref="IPropertyGridItem.Value"/>.
    /// </summary>
    [Parameter]
    public GridSort<TItem> ValueSort { get; set; } = s_defaultValueSort;

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
    public DataGridGeneratedHeaderType GenerateHeader { get; set; } = DataGridGeneratedHeaderType.Default;

    [Parameter]
    public string? Class { get; set; }

    [Parameter]
    public Dictionary<string, ComponentMetadata>? ValueComponents { get; set; }

    private ColumnResizeLabels _resizeLabels = ColumnResizeLabels.Default;
    private ColumnSortLabels _sortLabels = ColumnSortLabels.Default;

    protected override void OnInitialized()
    {
        (_resizeLabels, _sortLabels) = DashboardUIHelpers.CreateGridLabels(Loc);
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
