// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Model;
using Aspire.Dashboard.Otlp.Model;
using Aspire.Dashboard.Components.Tests.Shared;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Xunit;

namespace Aspire.Dashboard.Components.Tests.Controls;

[UseCulture("en-US")]
public class ChartFiltersTests : DashboardTestContext
{
    [Fact]
    public void AreAllValuesSelected_SetFalse_ClearsOnlyWhenAllValuesAreStored()
    {
        var dimensionFilter = new DimensionFilterViewModel { Name = "http.method" };
        dimensionFilter.Values.Add(new DimensionValueViewModel { Text = "GET", Value = "GET", });
        dimensionFilter.Values.Add(new DimensionValueViewModel { Text = "POST", Value = "POST", });
        dimensionFilter.SelectedValues.Add(dimensionFilter.Values[0]);
        dimensionFilter.SelectedValues.Add(dimensionFilter.Values[1]);

        Assert.True(dimensionFilter.AreAllValuesSelected);

        dimensionFilter.AreAllValuesSelected = false;

        Assert.Empty(dimensionFilter.SelectedValues);
    }

    [Fact]
    public void AreAllValuesSelected_SetFalse_DoesNotClearWhenPartiallySelected()
    {
        var dimensionFilter = new DimensionFilterViewModel { Name = "http.method" };
        dimensionFilter.Values.Add(new DimensionValueViewModel { Text = "GET", Value = "GET", });
        dimensionFilter.Values.Add(new DimensionValueViewModel { Text = "POST", Value = "POST", });
        dimensionFilter.SelectedValues.Add(dimensionFilter.Values[0]);

        Assert.Null(dimensionFilter.AreAllValuesSelected);

        dimensionFilter.AreAllValuesSelected = false;

        Assert.Single(dimensionFilter.SelectedValues);
        Assert.Equal("GET", dimensionFilter.SelectedValues.First().Value);
    }

    [Fact]
    public void AreAllValuesSelected_SetTrue_SelectsAllValues()
    {
        var dimensionFilter = new DimensionFilterViewModel { Name = "http.method" };
        dimensionFilter.Values.Add(new DimensionValueViewModel { Text = "GET", Value = "GET", });
        dimensionFilter.Values.Add(new DimensionValueViewModel { Text = "POST", Value = "POST", });
        dimensionFilter.SelectedValues.Add(dimensionFilter.Values[0]);

        dimensionFilter.AreAllValuesSelected = true;

        Assert.Equal(dimensionFilter.Values.Count, dimensionFilter.SelectedValues.Count);
        Assert.True(dimensionFilter.AreAllValuesSelected);
    }

    [Fact]
    public void OnTagSelectionChanged_RemovesValue_LeavesOthersSelected()
    {
        var dimensionFilter = new DimensionFilterViewModel { Name = "http.method" };
        var getValue = new DimensionValueViewModel { Text = "GET", Value = "GET", };
        var postValue = new DimensionValueViewModel { Text = "POST", Value = "POST", };
        dimensionFilter.Values.Add(getValue);
        dimensionFilter.Values.Add(postValue);
        dimensionFilter.SelectedValues.Add(getValue);
        dimensionFilter.SelectedValues.Add(postValue);

        dimensionFilter.OnTagSelectionChanged(getValue, isChecked: false);

        Assert.Single(dimensionFilter.SelectedValues);
        Assert.Contains(postValue, dimensionFilter.SelectedValues);
        Assert.DoesNotContain(getValue, dimensionFilter.SelectedValues);
    }

    [Fact]
    public void Render_AllValuesSelected_ShowsAllState()
    {
        SetupChartFilters();
        var dimensionFilter = CreateDimensionFilter();
        dimensionFilter.AreAllValuesSelected = true;

        var cut = RenderChartFilters(dimensionFilter);

        Assert.DoesNotContain("(None)", cut.Markup);
        Assert.Contains("aria-label=\"All tags\"", cut.Markup);
    }

    [Fact]
    public void Render_FilterValueTags_AreKeyboardAccessible()
    {
        SetupChartFilters();
        var dimensionFilter = CreateDimensionFilter();
        var changed = false;

        var cut = RenderChartFilters(dimensionFilter, _ => changed = true);
        var getTag = cut.FindAll(".filter-value-tag").Single(e => e.TextContent.Trim() == "GET");

        Assert.Equal("button", getTag.GetAttribute("role"));
        Assert.Equal("0", getTag.GetAttribute("tabindex"));

        getTag.KeyDown(new KeyboardEventArgs { Key = "Enter" });

        Assert.True(changed);
        Assert.Single(dimensionFilter.SelectedValues);
        Assert.Equal("GET", dimensionFilter.SelectedValues.Single().Text);
    }

    [Fact]
    public void GetOrderedValues_NumericValues_OrdersNumerically()
    {
        var dimensionFilter = new DimensionFilterViewModel { Name = "http.status_code" };
        dimensionFilter.Values.Add(new DimensionValueViewModel { Text = "200", Value = "200", });
        dimensionFilter.Values.Add(new DimensionValueViewModel { Text = "404", Value = "404", });
        dimensionFilter.Values.Add(new DimensionValueViewModel { Text = "50", Value = "50", });
        dimensionFilter.Values.Add(new DimensionValueViewModel { Text = "1000", Value = "1000", });

        var ordered = ChartFilterTags.GetOrderedValues(dimensionFilter.Values).Select(v => v.Text).ToList();

        Assert.Equal(["50", "200", "404", "1000"], ordered);
    }

    [Fact]
    public void GetOrderedValues_TextValues_OrdersAlphabetically()
    {
        var dimensionFilter = new DimensionFilterViewModel { Name = "http.method" };
        dimensionFilter.Values.Add(new DimensionValueViewModel { Text = "POST", Value = "POST", });
        dimensionFilter.Values.Add(new DimensionValueViewModel { Text = "GET", Value = "GET", });
        dimensionFilter.Values.Add(new DimensionValueViewModel { Text = "DELETE", Value = "DELETE", });

        var ordered = ChartFilterTags.GetOrderedValues(dimensionFilter.Values).Select(v => v.Text).ToList();

        Assert.Equal(["DELETE", "GET", "POST"], ordered);
    }

    private IRenderedComponent<ChartFilters> RenderChartFilters(DimensionFilterViewModel dimensionFilter, Action<DimensionFilterViewModel>? onDimensionValuesChanged = null)
    {
        return RenderComponent<ChartFilters>(builder =>
        {
            builder.Add(p => p.Instrument, CreateInstrument());
            builder.Add(p => p.InstrumentViewModel, new InstrumentViewModel());
            builder.Add(p => p.DimensionFilters, [dimensionFilter]);
            if (onDimensionValuesChanged is not null)
            {
                builder.Add(p => p.OnDimensionValuesChanged, EventCallback.Factory.Create(this, onDimensionValuesChanged));
            }
        });
    }

    private void SetupChartFilters()
    {
        FluentUISetupHelpers.AddCommonDashboardServices(this);
        FluentUISetupHelpers.SetupFluentDataGrid(this);
        FluentUISetupHelpers.SetupFluentCheckbox(this);
        FluentUISetupHelpers.SetupFluentAnchoredRegion(this);
        MetricsSetupHelpers.SetupChartContainer(this);
        FluentUISetupHelpers.SetupFluentUIComponents(this);
    }

    private static DimensionFilterViewModel CreateDimensionFilter()
    {
        var dimensionFilter = new DimensionFilterViewModel { Name = "http.method" };
        dimensionFilter.Values.Add(new DimensionValueViewModel { Text = "GET", Value = "GET", });
        dimensionFilter.Values.Add(new DimensionValueViewModel { Text = "POST", Value = "POST", });

        return dimensionFilter;
    }

    private static OtlpInstrumentData CreateInstrument()
    {
        return new OtlpInstrumentData
        {
            Summary = new OtlpInstrumentSummary
            {
                Name = "request-duration",
                Description = string.Empty,
                Unit = "ms",
                Type = OtlpInstrumentType.Sum,
                AggregationTemporality = OtlpAggregationTemporality.Cumulative,
                Parent = new OtlpScope("meter", string.Empty, [])
            },
            Dimensions = [],
            KnownAttributeValues = [],
            HasOverflow = false
        };
    }
}
