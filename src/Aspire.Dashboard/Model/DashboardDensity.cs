// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Dashboard.Model;

/// <summary>
/// Defines the dashboard's visual density.
/// </summary>
public enum DashboardDensity
{
    /// <summary>
    /// The default dashboard spacing.
    /// </summary>
    Comfortable,

    /// <summary>
    /// Reduced spacing for denser dashboard views.
    /// </summary>
    Compact
}

internal static class DashboardDensityExtensions
{
    public static string ToAttributeValue(this DashboardDensity density) => density switch
    {
        DashboardDensity.Compact => "compact",
        _ => "comfortable"
    };

    public static int MainGridItemSize(this DashboardDensity density) => density switch
    {
        DashboardDensity.Compact => 36,
        _ => 46
    };

    public static int TraceDetailGridItemSize(this DashboardDensity density) => density switch
    {
        DashboardDensity.Compact => 36,
        _ => 44
    };
}
