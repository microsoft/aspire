// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Configuration;
using Aspire.Dashboard.Utils;
using Microsoft.Extensions.Options;
using Microsoft.JSInterop;

namespace Aspire.Dashboard.Model;

/// <summary>
/// How much room each resource takes in the resource list.
/// </summary>
public enum ResourceListDensity
{
    // Keep the values stable for numeric preferences accepted by Enum.TryParse: 1 was Spacious, which was removed.
    /// <summary>
    /// Two lines per resource: the name, then its state and type.
    /// </summary>
    Comfortable = 0,

    /// <summary>
    /// One line per resource, with its state, type and persistence in aligned columns on the right.
    /// </summary>
    Compact = 2
}

/// <summary>
/// What the overview shows when all resources, a group or several resources are selected.
/// </summary>
public enum GroupOverview
{
    // Keep the remaining values stable for numeric preferences accepted by Enum.TryParse.
    /// <summary>
    /// The app's health, live activity, insights and recent problems.
    /// </summary>
    Summary = 1,

    /// <summary>
    /// A relationship graph of the resources.
    /// </summary>
    Graph
}

/// <summary>
/// The view preferences chosen in the settings. They're kept in the browser's local storage, and shared by the
/// settings panel and the views that apply them, which update as soon as a preference changes.
/// </summary>
public sealed class DashboardViewPreferences(ILocalStorage localStorage, IOptionsMonitor<DashboardOptions> options)
{
    private Task? _loadTask;
    private GroupOverview _groupOverview = GroupOverview.Summary;

    private const ResourceListDensity DefaultDensity = ResourceListDensity.Comfortable;

    /// <summary>
    /// Gets the resource list's density.
    /// </summary>
    public ResourceListDensity Density { get; private set; } = DefaultDensity;

    /// <summary>
    /// Gets what the overview of several resources shows.
    /// </summary>
    /// <remarks>
    /// A graph choice stored before Dashboard:UI:DisableResourceGraph was set shows the summary instead. The stored
    /// choice is kept, so the graph comes back if the setting is removed.
    /// </remarks>
    public GroupOverview GroupOverview
    {
        get => _groupOverview == GroupOverview.Graph && !IsGraphEnabled ? GroupOverview.Summary : _groupOverview;
        private set => _groupOverview = value;
    }

    /// <summary>
    /// Gets a value indicating whether the overview can show the resource graph, which Dashboard:UI:DisableResourceGraph turns off.
    /// </summary>
    public bool IsGraphEnabled => options.CurrentValue.UI.DisableResourceGraph != true;

    /// <summary>
    /// Raised when a preference changes.
    /// </summary>
    public event Action? Changed;

    /// <summary>
    /// Loads the stored preferences, once per circuit.
    /// </summary>
    public Task EnsureLoadedAsync() => _loadTask ??= LoadAsync();

    public async Task SetDensityAsync(ResourceListDensity density)
    {
        if (Density == density)
        {
            return;
        }

        Density = density;
        Changed?.Invoke();
        await localStorage.SetUnprotectedAsync(BrowserStorageKeys.ResourceListDensity, density.ToString()).ConfigureAwait(false);
    }

    public async Task SetGroupOverviewAsync(GroupOverview groupOverview)
    {
        // Graph deep links and shortcuts go through here, so this is where a disabled graph is enforced.
        if (GroupOverview == groupOverview || (groupOverview == GroupOverview.Graph && !IsGraphEnabled))
        {
            return;
        }

        GroupOverview = groupOverview;
        Changed?.Invoke();
        await localStorage.SetUnprotectedAsync(BrowserStorageKeys.GroupOverview, groupOverview.ToString()).ConfigureAwait(false);
    }

    private async Task LoadAsync()
    {
        try
        {
            var snapshot = await localStorage.ReadSnapshotAsync([
                BrowserStorageKeys.ResourceListDensity,
                BrowserStorageKeys.GroupOverview
            ]).ConfigureAwait(false);

            var densityTask = ReadDensityAsync(snapshot);
            var groupOverviewTask = ReadGroupOverviewAsync(snapshot);
            await Task.WhenAll(densityTask, groupOverviewTask).ConfigureAwait(false);

            var density = await densityTask.ConfigureAwait(false);
            var groupOverview = await groupOverviewTask.ConfigureAwait(false);
            if (density != Density || groupOverview != GroupOverview)
            {
                Density = density;
                GroupOverview = groupOverview;
                Changed?.Invoke();
            }
        }
        catch (JSDisconnectedException)
        {
            // The circuit is gone. Load again if another component asks.
            _loadTask = null;
        }
    }

    private async Task<ResourceListDensity> ReadDensityAsync(ILocalStorageSnapshot snapshot)
    {
        var result = snapshot.GetUnprotected<string>(BrowserStorageKeys.ResourceListDensity);
        // Rewrite the removed "Spacious" choice once so later loads don't need to migrate it again.
        if (result.Success && result.Value is "Spacious")
        {
            await localStorage.SetUnprotectedAsync(BrowserStorageKeys.ResourceListDensity, nameof(ResourceListDensity.Comfortable)).ConfigureAwait(false);
            return ResourceListDensity.Comfortable;
        }

        return result.Success && Enum.TryParse<ResourceListDensity>(result.Value, out var value) && Enum.IsDefined(value) ? value : DefaultDensity;
    }

    private async Task<GroupOverview> ReadGroupOverviewAsync(ILocalStorageSnapshot snapshot)
    {
        var result = snapshot.GetUnprotected<string>(BrowserStorageKeys.GroupOverview);
        // Earlier layouts were stored as "Cards" or "Resources". Replace those removed choices once,
        // so another opening cannot attempt to restore a layout that no longer exists.
        if (result.Success && result.Value is "Cards" or "Resources")
        {
            await localStorage.SetUnprotectedAsync(BrowserStorageKeys.GroupOverview, nameof(GroupOverview.Summary)).ConfigureAwait(false);
            return GroupOverview.Summary;
        }

        return result.Success && Enum.TryParse<GroupOverview>(result.Value, out var value) && Enum.IsDefined(value) ? value : GroupOverview.Summary;
    }
}
