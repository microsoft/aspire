// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Dashboard.Model;

/// <summary>
/// Compatibility shim for the removed FluentUI v4 ColumnResizeLabels.
/// These labels were used to localize the column resize menu.
/// </summary>
public sealed record ColumnResizeLabels
{
    public static ColumnResizeLabels Default { get; } = new();

    public string? ExactLabel { get; init; }
    public string? ResizeMenu { get; init; }
    public string? DiscreteLabel { get; init; }
    public string? GrowAriaLabel { get; init; }
    public string? ResetAriaLabel { get; init; }
    public string? ShrinkAriaLabel { get; init; }
    public string? SubmitAriaLabel { get; init; }
}

/// <summary>
/// Compatibility shim for the removed FluentUI v4 ColumnSortLabels.
/// These labels were used to localize the column sort menu.
/// </summary>
public sealed record ColumnSortLabels
{
    public static ColumnSortLabels Default { get; } = new();

    public string? SortMenu { get; init; }
    public string? SortMenuAscendingLabel { get; init; }
    public string? SortMenuDescendingLabel { get; init; }
}
