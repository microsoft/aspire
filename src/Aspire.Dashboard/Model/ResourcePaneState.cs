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
    /// Legacy mode for links to the former Tags view. The layout applies tag grouping in the Resources pane.
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
    /// Gets or sets the dashboard run the resource selection was last resolved in.
    /// </summary>
    /// <remarks>
    /// Switching between the live view and a recording re-creates the resources layout. Remembering the run, and
    /// the run-independent keys of the selected resources, lets the new layout carry the selection over even when
    /// resource names differ, such as replicas whose names end with a random suffix.
    /// </remarks>
    internal string? SelectionRunId { get; set; }

    /// <summary>
    /// Gets or sets the persistent keys (<see cref="ResourceViewModel.PersistentKey"/>) of the selected resources in
    /// <see cref="SelectionRunId"/>, by resource name.
    /// </summary>
    internal IReadOnlyDictionary<string, string> SelectionPersistentKeys { get; set; } = new Dictionary<string, string>();

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
