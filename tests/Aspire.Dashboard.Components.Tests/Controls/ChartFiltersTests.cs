// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Model;
using Xunit;

namespace Aspire.Dashboard.Components.Tests.Controls;

public class ChartFiltersTests
{
    [Fact]
    public void AreAllValuesSelected_SetFalse_ClearsOnlyWhenAllSelected()
    {
        // Arrange - all values selected
        var dimensionFilter = new DimensionFilterViewModel { Name = "http.method" };
        dimensionFilter.Values.Add(new DimensionValueViewModel { Text = "GET", Value = "GET" });
        dimensionFilter.Values.Add(new DimensionValueViewModel { Text = "POST", Value = "POST" });
        dimensionFilter.SelectedValues.Add(dimensionFilter.Values[0]);
        dimensionFilter.SelectedValues.Add(dimensionFilter.Values[1]);

        Assert.True(dimensionFilter.AreAllValuesSelected);

        // Act - set false when all are selected
        dimensionFilter.AreAllValuesSelected = false;

        // Assert - should clear
        Assert.Empty(dimensionFilter.SelectedValues);
    }

    [Fact]
    public void AreAllValuesSelected_SetFalse_DoesNotClearWhenPartiallySelected()
    {
        // This test verifies the fix for the FluentCheckbox ThreeState race condition.
        // FluentCheckbox with ThreeState=true can spuriously fire the setter with false
        // when the bound CheckState changes from true to null (intermediate state).
        // Our fix prevents clearing when AreAllValuesSelected is not true.

        // Arrange - only GET selected (partial selection, AreAllValuesSelected = null)
        var dimensionFilter = new DimensionFilterViewModel { Name = "http.method" };
        dimensionFilter.Values.Add(new DimensionValueViewModel { Text = "GET", Value = "GET" });
        dimensionFilter.Values.Add(new DimensionValueViewModel { Text = "POST", Value = "POST" });
        dimensionFilter.SelectedValues.Add(dimensionFilter.Values[0]); // Only GET

        Assert.Null(dimensionFilter.AreAllValuesSelected); // Partial selection = null

        // Act - simulate FluentCheckbox spuriously firing setter with false
        dimensionFilter.AreAllValuesSelected = false;

        // Assert - GET should still be selected (not cleared)
        Assert.Single(dimensionFilter.SelectedValues);
        Assert.Equal("GET", dimensionFilter.SelectedValues.First().Value);
    }

    [Fact]
    public void AreAllValuesSelected_SetTrue_SelectsAllValues()
    {
        // Arrange - no values selected
        var dimensionFilter = new DimensionFilterViewModel { Name = "http.method" };
        dimensionFilter.Values.Add(new DimensionValueViewModel { Text = "GET", Value = "GET" });
        dimensionFilter.Values.Add(new DimensionValueViewModel { Text = "POST", Value = "POST" });

        // Act
        dimensionFilter.AreAllValuesSelected = true;

        // Assert
        Assert.Equal(2, dimensionFilter.SelectedValues.Count);
        Assert.True(dimensionFilter.AreAllValuesSelected);
    }

    [Fact]
    public void OnTagSelectionChanged_RemovesValue_LeavesOthersSelected()
    {
        // This tests the normal flow when user unchecks a single filter value.

        // Arrange - both selected
        var dimensionFilter = new DimensionFilterViewModel { Name = "http.method" };
        var getValue = new DimensionValueViewModel { Text = "GET", Value = "GET" };
        var postValue = new DimensionValueViewModel { Text = "POST", Value = "POST" };
        dimensionFilter.Values.Add(getValue);
        dimensionFilter.Values.Add(postValue);
        dimensionFilter.SelectedValues.Add(getValue);
        dimensionFilter.SelectedValues.Add(postValue);

        // Act - uncheck GET
        dimensionFilter.OnTagSelectionChanged(getValue, isChecked: false);

        // Assert - only POST remains
        Assert.Single(dimensionFilter.SelectedValues);
        Assert.Contains(postValue, dimensionFilter.SelectedValues);
        Assert.DoesNotContain(getValue, dimensionFilter.SelectedValues);
    }
}
