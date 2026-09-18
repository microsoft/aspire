// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Dashboard.Model.HealthModel;

/// <summary>
/// Health state severity comparison and propagation helpers.
/// </summary>
public static class HealthStateExtensions
{
    /// <summary>
    /// Gets the relative severity of a state, where a higher number is worse.
    /// </summary>
    /// <remarks>
    /// <see cref="HealthState.Unknown"/> is deliberately the <em>least</em> severe state rather than a
    /// middling one. This matches Azure Monitor, where "Unknown = 0 is the lowest severity and can never beat
    /// any non-Unknown member" under a worst-of rollup. The practical consequence is that a child that has
    /// not reported yet never drags its parent down.
    /// </remarks>
    public static int Severity(this HealthState state) => state switch
    {
        HealthState.Unknown => 0,
        HealthState.Healthy => 1,
        HealthState.Degraded => 2,
        HealthState.Unhealthy => 3,
        _ => 0
    };

    /// <summary>Returns the more severe of two states.</summary>
    public static HealthState WorstOf(HealthState first, HealthState second)
        => first.Severity() >= second.Severity() ? first : second;

    /// <summary>
    /// Returns the most severe state in a sequence, or <see cref="HealthState.Unknown"/> when it is empty.
    /// </summary>
    public static HealthState WorstOf(IEnumerable<HealthState> states)
    {
        var worst = HealthState.Unknown;
        foreach (var state in states)
        {
            worst = WorstOf(worst, state);
        }

        return worst;
    }
}
