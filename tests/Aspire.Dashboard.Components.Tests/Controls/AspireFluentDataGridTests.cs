// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Linq.Expressions;
using Aspire.Dashboard.Components.Controls;
using Aspire.Dashboard.Components.Controls.Grid;
using Aspire.Dashboard.Components.Tests.Shared;
using Aspire.Dashboard.Model;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.FluentUI.AspNetCore.Components;
using Xunit;

namespace Aspire.Dashboard.Components.Tests.Controls;

public sealed class AspireFluentDataGridTests : DashboardTestContext
{
    private const string DataGridModulePath = "./_content/Microsoft.FluentUI.AspNetCore.Components/Components/DataGrid/FluentDataGrid.razor.js";
    private const string InitializeGrid = "Microsoft.FluentUI.Blazor.DataGrid.Initialize";
    private const string EnableColumnResizing = "Microsoft.FluentUI.Blazor.DataGrid.EnableColumnResizing";

    [Theory]
    [InlineData(true, "b,a,c")]
    [InlineData(false, "c,a,b")]
    public void EnumerableSort_PreservesMixedDirectionsAndComparers(bool ascending, string expected)
    {
        var items = new[] { (Name: "a", Value: 1), (Name: "b", Value: 2), (Name: "c", Value: 1) };
        var sort = EnumerableGridSort<(string Name, int Value)>.ByDescending(item => item.Value)
            .ThenAscending(item => item.Name, StringComparer.OrdinalIgnoreCase);

        Assert.Equal(expected.Split(','), sort.Apply(items, ascending).Select(item => item.Name));
    }

    [Theory]
    [InlineData(true, "first,second,last")]
    [InlineData(false, "last,first,second")]
    public void EnumerableSort_NullKeysAndTiesAreStable(bool ascending, string expected)
    {
        var items = new[] { (Name: "first", Value: (string?)null), (Name: "last", Value: "value"), (Name: "second", Value: (string?)null) };
        var sort = EnumerableGridSort<(string Name, string? Value)>.ByAscending(item => item.Value);

        Assert.Equal(expected.Split(','), sort.Apply(items, ascending).Select(item => item.Name));
    }

    [Theory]
    [InlineData(0, null, "3,1,2")]
    [InlineData(1, 1, "1")]
    [InlineData(1, null, "1,2")]
    [InlineData(0, 0, "")]
    [InlineData(10, 2, "")]
    public async Task EnumerableProvider_PagesWithoutChangingSourceOrder(int startIndex, int? count, string expected)
    {
        var provider = EnumerableGridItemsProvider.Create(() => new[] { 3, 1, 2 });
        var result = await provider(new GridItemsProviderRequest<int> { StartIndex = startIndex, Count = count });

        Assert.Equal(expected.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(int.Parse), result.Items);
        Assert.Equal(3, result.TotalItemCount);
    }

    [Fact]
    public async Task EnumerableProvider_SortsBeforePagingAndReadsCurrentItems()
    {
        var items = new List<int> { 3, 1, 2 };
        var provider = EnumerableGridItemsProvider.Create(() => items);
        var column = new AspireTemplateColumn<int>();
        ParameterView.FromDictionary(new Dictionary<string, object?> { [nameof(column.SortBy)] = EnumerableGridSort<int>.ByAscending(item => item) }).SetParameterProperties(column);
        var request = new GridItemsProviderRequest<int> { SortColumns = [new(column, Ascending: true)], StartIndex = 1, Count = 2 };

        var result = await provider(request);
        Assert.Equal([2, 3], result.Items);
        Assert.Equal(3, result.TotalItemCount);

        items.Add(0);
        result = await provider(request);
        Assert.Equal([1, 2], result.Items);
        Assert.Equal(4, result.TotalItemCount);
    }

    [Fact]
    public async Task EnumerableProvider_NullSourceIsEmpty()
    {
        var provider = EnumerableGridItemsProvider.Create<string>(() => null);
        var result = await provider(new GridItemsProviderRequest<string>());

        Assert.Empty(result.Items);
        Assert.Equal(0, result.TotalItemCount);
    }

    [Fact]
    public async Task EnumerableProvider_CancelledRequestDoesNotReadSource()
    {
        var provider = EnumerableGridItemsProvider.Create<int>(() => throw new InvalidOperationException("Source should not be read."));
        var request = new GridItemsProviderRequest<int> { CancellationToken = new CancellationToken(canceled: true) };

        await Assert.ThrowsAsync<OperationCanceledException>(async () => await provider(request));
    }

    [Fact]
    public async Task EnumerableProvider_RejectsQueryableSort()
    {
        var provider = EnumerableGridItemsProvider.Create(() => new[] { 1 });
        var column = new AspireTemplateColumn<int>();
        ParameterView.FromDictionary(new Dictionary<string, object?> { [nameof(column.SortBy)] = GridSort<int>.ByAscending(item => item) }).SetParameterProperties(column);

        await Assert.ThrowsAsync<InvalidOperationException>(async () => await provider(new GridItemsProviderRequest<int> { SortColumns = [new(column, Ascending: true)] }));
    }

    [Fact]
    public async Task PropertyGrid_SortSurvivesInPlaceSourceUpdateAndCanBeCleared()
    {
        FluentUISetupHelpers.AddCommonDashboardServices(this);
        FluentUISetupHelpers.SetupFluentDataGrid(this);
        FluentUISetupHelpers.SetupFluentMenu(this);
        var items = new List<EnvironmentVariableViewModel>
        {
            new("beta", "2", fromSpec: true),
            new("alpha", "1", fromSpec: true)
        };
        var cut = Render<PropertyGrid<EnvironmentVariableViewModel>>(builder => builder.Add(component => component.Items, items));
        var grid = cut.FindComponent<FluentDataGrid<EnvironmentVariableViewModel>>();
        var nameColumn = cut.FindComponents<AspireTemplateColumn<EnvironmentVariableViewModel>>()[0];

        await cut.InvokeAsync(() => grid.Instance.SortByColumnAsync(nameColumn.Instance, DataGridSortDirection.Ascending));
        cut.WaitForAssertion(() => Assert.Equal(["alpha", "beta"], cut.FindAll("td.nameColumn").Select(cell => cell.TextContent.Trim())));

        items.Add(new("aardvark", "0", fromSpec: true));
        cut.Render(builder => builder.Add(component => component.Items, items));
        cut.WaitForAssertion(() => Assert.Equal(["aardvark", "alpha", "beta"], cut.FindAll("td.nameColumn").Select(cell => cell.TextContent.Trim())));

        await cut.InvokeAsync(grid.Instance.RemoveSortByColumnAsync);
        cut.WaitForAssertion(() => Assert.Equal(["beta", "alpha", "aardvark"], cut.FindAll("td.nameColumn").Select(cell => cell.TextContent.Trim())));
    }

    [Fact]
    public async Task PropertyGrid_ValueSortUsesMaskedValuesAsNull()
    {
        FluentUISetupHelpers.AddCommonDashboardServices(this);
        FluentUISetupHelpers.SetupFluentDataGrid(this);
        FluentUISetupHelpers.SetupFluentMenu(this);
        var items = new[]
        {
            new EnvironmentVariableViewModel("visible", "a", fromSpec: true) { IsValueMasked = false },
            new EnvironmentVariableViewModel("masked", "z", fromSpec: true)
        };
        var cut = Render<PropertyGrid<EnvironmentVariableViewModel>>(builder => builder.Add(component => component.Items, items));
        var grid = cut.FindComponent<FluentDataGrid<EnvironmentVariableViewModel>>();
        var valueColumn = cut.FindComponents<AspireTemplateColumn<EnvironmentVariableViewModel>>()[1];

        await cut.InvokeAsync(() => grid.Instance.SortByColumnAsync(valueColumn.Instance, DataGridSortDirection.Ascending));
        cut.WaitForAssertion(() => Assert.Equal(["masked", "visible"], cut.FindAll("td.nameColumn").Select(cell => cell.TextContent.Trim())));

        await cut.InvokeAsync(() => grid.Instance.SortByColumnAsync(valueColumn.Instance, DataGridSortDirection.Descending));
        cut.WaitForAssertion(() => Assert.Equal(["visible", "masked"], cut.FindAll("td.nameColumn").Select(cell => cell.TextContent.Trim())));
    }

    [Fact]
    public void PropertyGrid_DisabledSortingPreservesSourceOrder()
    {
        FluentUISetupHelpers.AddCommonDashboardServices(this);
        FluentUISetupHelpers.SetupFluentDataGrid(this);
        FluentUISetupHelpers.SetupFluentMenu(this);
        var items = new[] { new EnvironmentVariableViewModel("beta", "2", fromSpec: true), new EnvironmentVariableViewModel("alpha", "1", fromSpec: true) };
        var cut = Render<PropertyGrid<EnvironmentVariableViewModel>>(builder => builder
            .Add(component => component.Items, items)
            .Add(component => component.IsNameSortable, false)
            .Add(component => component.IsValueSortable, false));

        Assert.All(cut.FindComponents<AspireTemplateColumn<EnvironmentVariableViewModel>>(), column => Assert.False(column.Instance.Sortable));
        cut.WaitForAssertion(() => Assert.Equal(["beta", "alpha"], cut.FindAll("td.nameColumn").Select(cell => cell.TextContent.Trim())));
    }

    [Fact]
    public void Render_LoadingContent_UsesCompactCenteredLayout()
    {
        FluentUISetupHelpers.AddCommonDashboardServices(this);
        FluentUISetupHelpers.SetupFluentDataGrid(this);

        var cut = Render<AspireFluentDataGrid<string>>(builder => builder
            .Add(component => component.Loading, true));

        var stack = cut.FindComponent<FluentStack>().Instance;
        Assert.Equal("3", stack.HorizontalGap);
        Assert.Equal(HorizontalAlignment.Center, stack.HorizontalAlignment);
        Assert.Equal(VerticalAlignment.Center, stack.VerticalAlignment);

        var progressRing = cut.FindComponent<AspireProgressRing>().Instance;
        Assert.Equal("24px", progressRing.Width);
        Assert.Equal("Loading...", cut.Find("tr[row-state='loading-content']").TextContent.Trim());
    }

    [Fact]
    public async Task RemovedWhileInitializing_IgnoresTheLateCallInsteadOfEndingTheCircuit()
    {
        var module = await RemoveResizableGridWhileItInitializesAsync<AspireFluentDataGrid<string>>();

        // The grid resumed after disposal and reached its released module, which rejected the call. The grid ignored
        // the rejection, so nothing reached the renderer, which would have ended the circuit.
        Assert.Equal([EnableColumnResizing], module.RejectedInvocations);
        Assert.False(Renderer.UnhandledException.IsCompleted);
    }

    // Fluent UI bug https://github.com/microsoft/fluentui-blazor/issues/5580, which AspireFluentDataGrid works around.
    // This test fails once the Fluent UI version used here fixes it: then remove the OnAfterRenderAsync override in
    // AspireFluentDataGrid, and this test.
    [Fact]
    public async Task FluentDataGrid_StillCallsIntoItsDisposedModuleWhenRemovedWhileInitializing()
    {
        var module = await RemoveResizableGridWhileItInitializesAsync<FluentDataGrid<string>>();

        Assert.Equal([EnableColumnResizing], module.RejectedInvocations);
        Assert.IsType<ObjectDisposedException>(await Renderer.UnhandledException.DefaultTimeout());
    }

    // Renders a grid with resizable columns, removes it while its first OnAfterRenderAsync awaits the grid's JavaScript
    // initialization, and then lets the initialization complete. This follows the order seen in the dashboard: the grid
    // renders its rows while its JavaScript module is still loading, which leaves the column setup for later, and is
    // removed after the module arrives but before the grid's initialization returns.
    private async Task<TestJSObjectReference> RemoveResizableGridWhileItInitializesAsync<TGrid>() where TGrid : FluentDataGrid<string>
    {
        FluentUISetupHelpers.AddCommonDashboardServices(this);
        FluentUISetupHelpers.SetupFluentUIComponents(this);
        var initializeStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseInitialize = new TaskCompletionSource();
        var module = new TestJSObjectReference
        {
            BeforeInvokeAsync = async identifier =>
            {
                if (identifier == InitializeGrid)
                {
                    initializeStarted.TrySetResult();
                    await releaseInitialize.Task;
                }
            }
        };
        var import = TestJSObjectReference.SetupImport(this, DataGridModulePath);

        var cut = Render<TGrid>(builder => builder
            .Add(grid => grid.ItemsProvider, EnumerableGridItemsProvider.Create(() => new[] { "value" }))
            .Add(grid => grid.ResizableColumns, true)
            .Add(grid => grid.ChildContent, RenderColumn));

        // The render that shows the rows comes before the module, so it skips the column setup and leaves it for later.
        cut.WaitForAssertion(() => Assert.Equal("value", cut.Find("[role=gridcell]").TextContent));
        await Renderer.Dispatcher.InvokeAsync(() => { });
        Assert.Empty(module.Invocations);

        import.SetResult(module);
        await initializeStarted.Task.DefaultTimeout();

        // Removing the grid disposes it, and its module, while the initialization is pending. This test resumes on the
        // thread pool while the dispatcher may still be finishing the grid's work item, so the removal runs on the
        // dispatcher: from another thread, DisposeComponentsAsync queues the removal there but clears its list of
        // rendered components right away, and the queued removal can then find nothing to remove.
        await Renderer.Dispatcher.InvokeAsync(DisposeComponentsAsync);
        Assert.Equal(1, module.DisposeCount);

        // The initialization completes after disposal, so the grid's OnAfterRenderAsync resumes. Its continuation is
        // posted to the renderer's dispatcher, and runs before this empty work item queued after it.
        releaseInitialize.SetResult();
        await Renderer.Dispatcher.InvokeAsync(() => { });
        return module;

        static void RenderColumn(RenderTreeBuilder builder)
        {
            builder.OpenComponent<PropertyColumn<string, string>>(0);
            builder.AddComponentParameter(1, nameof(PropertyColumn<string, string>.Title), "Value");
            builder.AddComponentParameter(2, nameof(PropertyColumn<string, string>.Property), (Expression<Func<string, string>>)(value => value));
            builder.CloseComponent();
        }
    }
}