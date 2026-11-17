// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.AspNetCore.Components;
using Microsoft.FluentUI.AspNetCore.Components;
using Microsoft.JSInterop;

namespace Aspire.Dashboard.Components.Controls.Grid;

/// <summary>
/// A <see cref="FluentDataGrid{TGridItem}"/> that a removal during its initialization can't use to end the circuit.
/// Dashboard grids use it, or <see cref="AspireFluentDataGrid{TGridItem}"/>, instead of <see cref="FluentDataGrid{TGridItem}"/>.
/// </summary>
/// <remarks>
/// Workaround for https://github.com/microsoft/fluentui-blazor/issues/5580. Once the Fluent UI version used here
/// includes the fix, remove this class: make <see cref="AspireFluentDataGrid{TGridItem}"/> derive from
/// <see cref="FluentDataGrid{TGridItem}"/> again, use <c>FluentDataGrid</c> where markup uses <c>DisposalSafeFluentDataGrid</c>,
/// and remove DisposalSafeFluentDataGridTests. Its <c>FluentDataGrid_StillCallsIntoItsDisposedModuleWhenRemovedWhileInitializing</c>
/// test fails when the fix arrives.
/// </remarks>
[CascadingTypeParameter(nameof(TGridItem))]
public class DisposalSafeFluentDataGrid<TGridItem> : FluentDataGrid<TGridItem>
{
    // Fluent UI's own disposal flag (IFluentComponentBase.IsDisposed) is internal.
    private bool _disposalStarted;

    public DisposalSafeFluentDataGrid(LibraryConfiguration configuration) : base(configuration)
    {
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        try
        {
            await base.OnAfterRenderAsync(firstRender);
        }
        catch (ObjectDisposedException) when (_disposalStarted)
        {
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

        if (_disposalStarted)
        {
            await StopGridControllerAfterDisposalAsync();
        }
    }

    // Fluent UI stops the grid's JavaScript controller when the grid is disposed, but only a controller that has arrived.
    // When disposal runs while the grid's Initialize call is pending, the controller arrives afterwards, whether or not
    // the render then fails, and keeps the removed grid registered in the browser with its click, mousedown and keydown
    // listeners on document.body. Fluent UI's controller cleanup is DisposeAsync(IJSObjectReference): it stops and
    // releases the controller once and does nothing after that, so it's run again here once a render that resumed after
    // disposal completes. Its argument is unused by FluentDataGrid.
    private async Task StopGridControllerAfterDisposalAsync()
    {
        if (!JSModule.Imported)
        {
            return;
        }

        try
        {
            await DisposeAsync(JSModule.ObjectReference);
        }
        catch (Exception ex) when (ex is JSDisconnectedException or OperationCanceledException)
        {
            // The circuit is gone, and the browser's controller with it.
        }
    }

    public override ValueTask DisposeAsync()
    {
        // Set before Fluent UI releases the grid's JavaScript module, so OnAfterRenderAsync can tell that a rejected
        // call came from a grid that's being removed.
        _disposalStarted = true;
        return base.DisposeAsync();
    }
}
