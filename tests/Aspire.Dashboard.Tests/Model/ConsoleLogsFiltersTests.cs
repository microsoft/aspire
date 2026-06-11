// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using Aspire.Dashboard.Model;
using Xunit;

namespace Aspire.Dashboard.Tests.Model;

public sealed class ConsoleLogsFiltersTests
{
    [Fact]
    public void Serialize()
    {
        // Arrange
        var filters = new ConsoleLogsFilters
        {
            FilterAllLogsDate = new DateTime(2023, 1, 2, 3, 4, 5, DateTimeKind.Utc),
            FilterResourceLogsDates = new Dictionary<string, DateTime>
            {
                ["test-abc"] = new DateTime(2023, 1, 2, 3, 4, 5, DateTimeKind.Utc)
            }
        };

        // Act
        var json = JsonSerializer.Serialize(filters);
        var deserialized = JsonSerializer.Deserialize<ConsoleLogsFilters>(json)!;

        // Assert
        Assert.Equal(filters.FilterAllLogsDate, deserialized.FilterAllLogsDate);
        Assert.Equivalent(filters.FilterResourceLogsDates, deserialized.FilterResourceLogsDates);
    }

    [Fact]
    public void WithResourceCleared_PreservesExistingFilters()
    {
        // Arrange
        var existingAllLogsDate = new DateTime(2023, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var existingResourceDate = new DateTime(2023, 1, 2, 0, 0, 0, DateTimeKind.Utc);
        var filters = new ConsoleLogsFilters
        {
            FilterAllLogsDate = existingAllLogsDate,
            FilterResourceLogsDates = new Dictionary<string, DateTime>
            {
                ["existing-resource"] = existingResourceDate
            }
        };

        // Act
        var newResourceDate = new DateTime(2023, 1, 3, 0, 0, 0, DateTimeKind.Utc);
        var updatedFilters = filters.WithResourceCleared("new-resource", newResourceDate);

        // Assert - existing FilterAllLogsDate is preserved
        Assert.Equal(existingAllLogsDate, updatedFilters.FilterAllLogsDate);

        // Assert - existing resource filter is preserved
        Assert.True(updatedFilters.TryGetResourceFilterDate("existing-resource", out var existingDate));
        Assert.Equal(existingResourceDate, existingDate);

        // Assert - new resource filter is added
        Assert.True(updatedFilters.TryGetResourceFilterDate("new-resource", out var newDate));
        Assert.Equal(newResourceDate, newDate);
    }

    [Fact]
    public void TryGetResourceFilterDate_ReturnsFalse_WhenResourceNotFound()
    {
        // Arrange
        var filters = new ConsoleLogsFilters { FilterAllLogsDate = null, FilterResourceLogsDates = new Dictionary<string, DateTime>() };

        // Act & Assert
        Assert.False(filters.TryGetResourceFilterDate("non-existent", out var filterDate));
        Assert.Null(filterDate);
    }
}
