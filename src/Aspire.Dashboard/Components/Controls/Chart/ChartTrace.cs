// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Dashboard.Components.Controls.Chart;

public sealed class ChartTrace
{
    public int? Percentile { get; init; }
    public required string Name { get; set; }
    public List<double?> Values { get; } = new();
    public List<double?> DiffValues { get; } = new();
    public List<string?> Tooltips { get; } = new();
}
