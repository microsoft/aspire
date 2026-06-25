// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Extensions;
using Aspire.Dashboard.Resources;
using Humanizer;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Localization;

namespace Aspire.Dashboard.Model;

internal class ResourceStateViewModel(string text)
{
    public string Text { get; } = text;

    /// <summary>
    /// Gets the text for the state column. The accompanying status color/dot is sourced from
    /// <see cref="Components.Deck.ResourceStateTone"/> (the single source of truth for state tone).
    /// </summary>
    internal static ResourceStateViewModel GetStateViewModel(ResourceViewModel resource, IStringLocalizer<Columns> loc)
    {
        return new ResourceStateViewModel(GetStateText(resource, loc));
    }

    /// <summary>
    /// Gets the tooltip for a cell in the state column of the resource grid.
    /// </summary>
    /// <remarks>
    /// This is a static method so it can be called at the level of the parent column.
    /// </remarks>
    internal static string GetResourceStateTooltip(ResourceViewModel resource, IStringLocalizer<Columns> loc, IEnumerable<ResourceViewModel>? allResources = null)
    {
        if (resource.IsFailedToStart())
        {
            return loc.GetString(nameof(Columns.StateColumnResourceFailedToStart), resource.ResourceType);
        }
        else if (resource.IsStopped())
        {
            if (resource.TryGetExitCode(out var exitCode) && exitCode is not 0)
            {
                // Process completed unexpectedly, hence the non-zero code. This is almost certainly an error, so warn users.
                return loc.GetString(nameof(Columns.StateColumnResourceExitedUnexpectedly), resource.ResourceType, exitCode);
            }
            else
            {
                // Process completed, which may not have been unexpected.
                return loc.GetString(nameof(Columns.StateColumnResourceExited), resource.ResourceType);
            }
        }
        else if (resource is { KnownState: KnownResourceState.Running, HealthStatus: not HealthStatus.Healthy })
        {
            // Resource is running but not healthy (initializing).
            return loc[nameof(Columns.RunningAndUnhealthyResourceStateToolTip)];
        }
        else if (resource.IsRuntimeUnhealthy() && resource.IsContainer())
        {
            // DCP reports the container runtime is unhealthy. Most likely the container runtime (e.g. Docker) isn't running.
            return loc[nameof(Columns.StateColumnResourceContainerRuntimeUnhealthy)];
        }
        else if (resource.IsWaiting())
        {
            if (allResources is not null
                ? resource.TryGetResolvedWaitingForDependencies(allResources, out var dependencies)
                : resource.TryGetWaitingForDependencies(out dependencies))
            {
                return loc.GetString(nameof(Columns.StateColumnResourceWaitingFor), string.Join(", ", dependencies));
            }

            return loc[nameof(Columns.StateColumnResourceWaiting)];
        }
        else if (resource.IsNotStarted())
        {
            return loc[nameof(Columns.StateColumnResourceNotStarted)];
        }

        // Fallback to text displayed in column.
        return GetStateText(resource, loc);
    }

    private static string GetStateText(ResourceViewModel resource, IStringLocalizer<Columns> loc)
    {
        return resource switch
        {
            { State: null or "" } => loc[Columns.UnknownStateLabel],
            { KnownState: KnownResourceState.Running, HealthStatus: not HealthStatus.Healthy } => $"{resource.State.Humanize()} ({(resource.HealthStatus ?? HealthStatus.Unhealthy).Humanize()})",
            _ => resource.State.Humanize()
        };
    }
}
