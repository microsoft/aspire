// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.FluentUI.AspNetCore.Components;
using Icons = Microsoft.FluentUI.AspNetCore.Components.Icons;

namespace Aspire.Dashboard.Model.HealthModel;

/// <summary>
/// Maps health model states to the icons and colors used to render them.
/// </summary>
/// <remarks>
/// Deliberately reuses the same icon and color pairs as <see cref="ResourceIconHelpers.GetHealthStatusIcon"/>
/// so a resource shows the same visual state on the resources page and in the health model.
/// </remarks>
public static class HealthModelIconHelpers
{
    /// <summary>Gets the icon and color used to represent a health state.</summary>
    public static (Icon Icon, Color Color) GetHealthStateIcon(HealthState state) => state switch
    {
        HealthState.Healthy => (new Icons.Filled.Size16.Heart(), Color.Success),
        HealthState.Degraded => (new Icons.Filled.Size16.HeartBroken(), Color.Warning),
        HealthState.Unhealthy => (new Icons.Filled.Size16.HeartBroken(), Color.Error),
        _ => (new Icons.Regular.Size16.CircleHint(), Color.Info)
    };
}
