// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Components.Controls;
using Aspire.Dashboard.Components.Controls.Grid;
using Aspire.Dashboard.Components.Tests.Shared;
using Aspire.Dashboard.Model;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.FluentUI.AspNetCore.Components;
using Xunit;

namespace Aspire.Dashboard.Components.Tests.Controls;

public sealed class AspireFluentDataGridTests : DashboardTestContext
{
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
        var request = new GridItemsProviderRequest<int> { SortByColumn = column, SortByAscending = true, StartIndex = 1, Count = 2 };

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

        await Assert.ThrowsAsync<InvalidOperationException>(async () => await provider(new GridItemsProviderRequest<int> { SortByColumn = column }));
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
        var cut = RenderComponent<PropertyGrid<EnvironmentVariableViewModel>>(builder => builder.Add(component => component.Items, items));
        var grid = cut.FindComponent<FluentDataGrid<EnvironmentVariableViewModel>>();
        var nameColumn = cut.FindComponents<AspireTemplateColumn<EnvironmentVariableViewModel>>()[0];

        await cut.InvokeAsync(() => grid.Instance.SortByColumnAsync(nameColumn.Instance, DataGridSortDirection.Ascending));
        cut.WaitForAssertion(() => Assert.Equal(["alpha", "beta"], cut.FindAll("td.nameColumn").Select(cell => cell.TextContent.Trim())));

        items.Add(new("aardvark", "0", fromSpec: true));
        cut.SetParametersAndRender(builder => builder.Add(component => component.Items, items));
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
        var cut = RenderComponent<PropertyGrid<EnvironmentVariableViewModel>>(builder => builder.Add(component => component.Items, items));
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
        var cut = RenderComponent<PropertyGrid<EnvironmentVariableViewModel>>(builder => builder
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

        var cut = RenderComponent<AspireFluentDataGrid<string>>(builder => builder
            .Add(component => component.Loading, true));

        var stack = cut.FindComponent<FluentStack>().Instance;
        Assert.Equal("3", stack.HorizontalGap);
        Assert.Equal(HorizontalAlignment.Center, stack.HorizontalAlignment);
        Assert.Equal(VerticalAlignment.Center, stack.VerticalAlignment);

        var progressRing = cut.FindComponent<AspireProgressRing>().Instance;
        Assert.Equal("24px", progressRing.Width);
        Assert.Equal("Loading...", cut.Find("tr[row-state='loading-content']").TextContent.Trim());
    }
}