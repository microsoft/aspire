// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Dashboard.Model.ManageData;

/// <summary>
/// Represents an item in the manage data grid, which can be either a resource row or a nested data row.
/// </summary>
public sealed class ManageDataGridItem
{
    public ResourceDataRow? ResourceRow { get; init; }
    public TelemetryDataRow? NestedRow { get; init; }
    public string? ParentResourceName { get; init; }

    public int Depth { get; init; }

    public bool IsResourceRow => ResourceRow is not null;
    public bool IsNestedRow => NestedRow is not null;
}
