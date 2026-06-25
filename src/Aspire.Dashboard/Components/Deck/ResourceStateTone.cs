// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Extensions;
using Aspire.Dashboard.Model;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Aspire.Dashboard.Components.Deck;

/// <summary>
/// Maps a resource's state to a Deck status "tone" CSS class (<c>success</c>/<c>info</c>/
/// <c>warning</c>/<c>error</c>/<c>neutral</c>) used by the <see cref="StateDot"/> primitive.
/// </summary>
/// <remarks>
/// This mirrors the cascade in <see cref="ResourceStateViewModel"/>.GetStateIcon so the Deck-styled
/// dashboard colors states identically to the legacy Fluent grid, but without depending on Fluent's
/// <c>Color</c>/<c>Icon</c> types. Keep the two in sync.
/// </remarks>
internal static class ResourceStateTone
{
    /// <summary>The neutral tone (grey), used when no other classification applies.</summary>
    public const string Neutral = "neutral";

    /// <summary>Classifies <paramref name="resource"/> into a Deck tone class.</summary>
    public static string Get(ResourceViewModel resource)
    {
        if (resource.IsStopped())
        {
            if (resource.TryGetExitCode(out var exitCode) && exitCode is not 0)
            {
                return "error";
            }

            if (resource.IsFinishedState() || resource.IsExitedState())
            {
                return "info";
            }

            return "warning";
        }

        if (resource.IsUnusableTransitoryState() || resource.IsUnknownState() || resource.IsNotStarted())
        {
            return "info";
        }

        if (resource.IsRuntimeUnhealthy())
        {
            return "warning";
        }

        if (resource.HasNoState())
        {
            return "info";
        }

        if (!string.IsNullOrEmpty(resource.StateStyle))
        {
            return resource.StateStyle switch
            {
                "warning" => "warning",
                "error" => "error",
                "success" => "success",
                "info" => "info",
                _ => Neutral
            };
        }

        if (resource.HealthStatus is HealthStatus.Unhealthy or HealthStatus.Degraded)
        {
            return "warning";
        }

        return "success";
    }
}
