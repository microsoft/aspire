// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Dashboard.Model;

/// <summary>
/// What the resource list next to resource pages displays.
/// </summary>
public enum ResourcePaneMode
{
    /// <summary>
    /// Resources ordered by type, with nested resources under their parent.
    /// </summary>
    Resources,

    /// <summary>
    /// Resources grouped by the tags users assign to them.
    /// </summary>
    Tags,

    /// <summary>
    /// Services sending telemetry that aren't part of the AppHost model.
    /// </summary>
    Telemetry
}

/// <summary>
/// Shares the resource list's mode between the resources layout, which owns the list, and the main navigation, which
/// highlights the entry for the current mode.
/// </summary>
public sealed class ResourcePaneState
{
    /// <summary>
    /// Gets the resource list's mode.
    /// </summary>
    public ResourcePaneMode Mode { get; private set; }

    /// <summary>
    /// Raised when <see cref="Mode"/> changes.
    /// </summary>
    public event Action? Changed;

    /// <summary>
    /// Sets the resource list's mode.
    /// </summary>
    public void SetMode(ResourcePaneMode mode)
    {
        if (Mode == mode)
        {
            return;
        }

        Mode = mode;
        Changed?.Invoke();
    }
}
