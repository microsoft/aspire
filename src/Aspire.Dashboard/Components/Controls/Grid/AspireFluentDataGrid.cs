// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Resources;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.FluentUI.AspNetCore.Components;
using Microsoft.Extensions.Localization;

namespace Aspire.Dashboard.Components.Controls.Grid;

[CascadingTypeParameter(nameof(TGridItem))]
public class AspireFluentDataGrid<TGridItem> : FluentDataGrid<TGridItem>
{
    // Fluent UI's own disposal flag (IFluentComponentBase.IsDisposed) is internal. See OnAfterRenderAsync.
    private bool _disposalStarted;

    public AspireFluentDataGrid(LibraryConfiguration configuration) : base(configuration)
    {
        LoadingContent = RenderLoadingContent;
    }

    [Inject]
    public required IStringLocalizer<ControlsStrings> Loc { get; init; }

    protected override void BuildRenderTree(RenderTreeBuilder builder)
    {
        builder.AddContent(0, (RenderFragment)(gridBuilder => base.BuildRenderTree(gridBuilder)));
        builder.OpenElement(1, "aspire-grid-tooltips");
        builder.AddAttribute(2, "grid", Id);
        builder.AddAttribute(3, "hidden", true);
        builder.CloseElement();
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        try
        {
            await base.OnAfterRenderAsync(firstRender);
        }
        catch (ObjectDisposedException) when (_disposalStarted)
        {
            // Workaround for https://github.com/microsoft/fluentui-blazor/issues/5580. Once the Fluent UI version used
            // here includes the fix, remove this override, the DisposeAsync override and _disposalStarted, and
            // AspireFluentDataGridTests.FluentDataGrid_StillCallsIntoItsDisposedModuleWhenRemovedWhileInitializing,
            // which fails when the fix arrives.
            //
            // FluentDataGrid.OnAfterRenderAsync awaits JavaScript calls, such as initializing the grid. A grid removed
            // during one of them, for example by a layout change right after it rendered, resumes after disposal and
            // calls into the JavaScript module that disposal released: FluentJSModule.Imported stays true after the
            // module is disposed, so the grid's own check doesn't catch it. The ObjectDisposedException then escapes
            // OnAfterRenderAsync, and Blazor Server ends the circuit, which stops every view in the browser tab.
            //
            // The grid is gone, so the calls it skips here (column resizing and reordering, auto-fit, popup position)
            // have nothing left to set up. Only that case is ignored: the same exception while the grid is still in
            // use is a real error and still surfaces.
        }
    }

    public override ValueTask DisposeAsync()
    {
        // Set before Fluent UI releases the grid's JavaScript module, so OnAfterRenderAsync can tell that a rejected
        // call came from a grid that's being removed.
        _disposalStarted = true;
        return base.DisposeAsync();
    }

    /// <summary>
    /// Refreshes virtualized data and renders this grid when the refresh originates outside a Blazor event.
    /// </summary>
    public async Task RefreshDataAndRenderAsync()
    {
        await RefreshDataAsync(force: true);
        StateHasChanged();
    }

    private void RenderLoadingContent(RenderTreeBuilder builder)
    {
        builder.OpenComponent<FluentStack>(0);
        builder.AddComponentParameter(1, nameof(FluentStack.HorizontalGap), "3");
        builder.AddComponentParameter(2, nameof(FluentStack.HorizontalAlignment), HorizontalAlignment.Center);
        builder.AddComponentParameter(3, nameof(FluentStack.VerticalAlignment), VerticalAlignment.Center);
        builder.AddAttribute(4, nameof(FluentStack.ChildContent), (RenderFragment)(contentBuilder =>
        {
            contentBuilder.OpenComponent<AspireProgressRing>(5);
            contentBuilder.AddComponentParameter(6, nameof(AspireProgressRing.Width), "24px");
            contentBuilder.AddComponentParameter(7, nameof(AspireProgressRing.AriaLabel), Loc[nameof(ControlsStrings.Loading)].Value);
            contentBuilder.CloseComponent();
            contentBuilder.OpenElement(8, "div");
            contentBuilder.AddContent(9, Loc[nameof(ControlsStrings.Loading)]);
            contentBuilder.CloseElement();
        }));
        builder.CloseComponent();
    }
}

/// <summary>
/// Supplies in-memory grid data without IQueryable or expression compilation.
/// </summary>
internal static class EnumerableGridItemsProvider
{
    public static GridItemsProvider<TItem> Create<TItem>(Func<IEnumerable<TItem>?> getItems)
    {
        // A new callback lets Fluent detect a data-source change when the parent renders, including
        // filters and in-place collection updates. Read the source at request time for virtualization.
        return request =>
        {
            request.CancellationToken.ThrowIfCancellationRequested();
            var items = ApplySorting(getItems() ?? [], request).ToList();
            return ValueTask.FromResult(GetPage(items, request));
        };
    }

    public static IEnumerable<TItem> ApplySorting<TItem>(IEnumerable<TItem> items, GridItemsProviderRequest<TItem> request)
    {
        request.CancellationToken.ThrowIfCancellationRequested();
        if (request.SortColumns is not [var sortColumn, ..])
        {
            return items;
        }

        return sortColumn.Column.SortBy switch
        {
            EnumerableGridSort<TItem> sort => sort.Apply(items, sortColumn.Ascending),
            _ => throw new InvalidOperationException("In-memory grid columns must use EnumerableGridSort.")
        };
    }

    public static GridItemsProviderResult<TItem> GetPage<TItem>(IReadOnlyCollection<TItem> items, GridItemsProviderRequest<TItem> request)
    {
        request.CancellationToken.ThrowIfCancellationRequested();
        var page = items.Skip(request.StartIndex);
        if (request.Count is { } count)
        {
            page = page.Take(count);
        }

        return GridItemsProviderResult.From(page.ToArray(), items.Count);
    }
}

/// <summary>
/// Defines delegate-based sorting for grids using EnumerableGridItemsProvider.
/// </summary>
internal sealed class EnumerableGridSort<TItem> : IGridSort<TItem>
{
    private readonly Func<IEnumerable<TItem>, bool, IOrderedEnumerable<TItem>> _sort;

    private EnumerableGridSort(Func<IEnumerable<TItem>, bool, IOrderedEnumerable<TItem>> sort)
    {
        _sort = sort;
    }

    public static EnumerableGridSort<TItem> ByAscending<TKey>(Func<TItem, TKey> selector, IComparer<TKey>? comparer = null)
        => new((items, ascending) => ascending ? items.OrderBy(selector, comparer) : items.OrderByDescending(selector, comparer));

    public static EnumerableGridSort<TItem> ByDescending<TKey>(Func<TItem, TKey> selector, IComparer<TKey>? comparer = null)
        => new((items, ascending) => ascending ? items.OrderByDescending(selector, comparer) : items.OrderBy(selector, comparer));

    public EnumerableGridSort<TItem> ThenAscending<TKey>(Func<TItem, TKey> selector, IComparer<TKey>? comparer = null)
        => new((items, ascending) => ascending ? _sort(items, ascending).ThenBy(selector, comparer) : _sort(items, ascending).ThenByDescending(selector, comparer));

    public EnumerableGridSort<TItem> ThenDescending<TKey>(Func<TItem, TKey> selector, IComparer<TKey>? comparer = null)
        => new((items, ascending) => ascending ? _sort(items, ascending).ThenByDescending(selector, comparer) : _sort(items, ascending).ThenBy(selector, comparer));

    public IOrderedEnumerable<TItem> Apply(IEnumerable<TItem> items, bool ascending) => _sort(items, ascending);

    // Fluent columns expose IGridSort, but these query-provider APIs cannot represent arbitrary
    // delegates or custom comparers. Only the ItemsProvider's enumerable path is supported.
    IOrderedQueryable<TItem> IGridSort<TItem>.Apply(IQueryable<TItem> queryable, bool ascending)
        => throw new NotSupportedException("Use EnumerableGridItemsProvider to apply in-memory sorting.");

    IReadOnlyCollection<SortedProperty> IGridSort<TItem>.ToPropertyList(bool ascending)
        => throw new NotSupportedException("Delegate-based sorting cannot be represented as property names.");
}
