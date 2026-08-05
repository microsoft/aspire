// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Dashboard.Components.Controls.Chart;

internal sealed class ChartData
{
    public required List<ChartTrace> Traces { get; init; }
    public required List<DateTimeOffset> XValues { get; init; }
    public required List<ChartExemplar> Exemplars { get; init; }
}
