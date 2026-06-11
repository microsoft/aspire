// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;

namespace Aspire.Dashboard.Model;

/// <summary>
/// Immutable filters for console logs. This type is serialized to browser storage.
/// </summary>
public sealed class ConsoleLogsFilters
{
    public static readonly ConsoleLogsFilters Default = new()
    {
        FilterAllLogsDate = null,
        FilterResourceLogsDates = new Dictionary<string, DateTime>()
    };

    private Dictionary<string, DateTime> _filterResourceLogsDates = default!;

    public required DateTime? FilterAllLogsDate { get; init; }

    public required IReadOnlyDictionary<string, DateTime> FilterResourceLogsDates
    {
        get => _filterResourceLogsDates;
        init
        {
            _filterResourceLogsDates = value is Dictionary<string, DateTime> newDictionary
                ? newDictionary
                : new Dictionary<string, DateTime>(value, StringComparers.ResourceName);
        }
    }

    /// <summary>
    /// Creates new filters with all logs cleared at the specified date.
    /// </summary>
    public static ConsoleLogsFilters CreateClearAll(DateTime clearDate) =>
        new() { FilterAllLogsDate = clearDate, FilterResourceLogsDates = new Dictionary<string, DateTime>() };

    /// <summary>
    /// Creates new filters based on this instance with a specific resource cleared at the specified date.
    /// </summary>
    public ConsoleLogsFilters WithResourceCleared(string resourceName, DateTime clearDate)
    {
        var newDictionary = new Dictionary<string, DateTime>(_filterResourceLogsDates, StringComparers.ResourceName)
        {
            [resourceName] = clearDate
        };
        return new ConsoleLogsFilters { FilterAllLogsDate = FilterAllLogsDate, FilterResourceLogsDates = newDictionary };
    }

    /// <summary>
    /// Tries to get the filter date for a specific resource.
    /// </summary>
    public bool TryGetResourceFilterDate(string resourceName, [NotNullWhen(true)] out DateTime? filterDate)
    {
        if (_filterResourceLogsDates.TryGetValue(resourceName, out var date))
        {
            filterDate = date;
            return true;
        }

        filterDate = null;
        return false;
    }
}
