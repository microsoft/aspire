// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Otlp.Model;

namespace Aspire.Dashboard.Model;

/// <summary>
/// Matches instruments against the metrics page's text filter. The group list and the cards use the same rule,
/// so a group's count always equals the number of cards shown when it's selected.
/// </summary>
public static class MetricFilter
{
    /// <summary>
    /// Gets whether the instrument matches the filter by its meter name, its name or its description.
    /// A meter name match keeps all of the meter's instruments, so filtering by "AspNetCore" shows that whole meter.
    /// </summary>
    public static bool Matches(OtlpInstrumentSummary instrument, string filter)
    {
        filter = filter.Trim();
        return filter.Length == 0
            || instrument.Parent.Name.Contains(filter, StringComparisons.UserTextSearch)
            || instrument.Name.Contains(filter, StringComparisons.UserTextSearch)
            || instrument.Description.Contains(filter, StringComparisons.UserTextSearch);
    }
}
