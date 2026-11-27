// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Components.Controls;
using Aspire.Dashboard.Components.CustomIcons;
using Aspire.Dashboard.Components.Layout;
using Aspire.Dashboard.Model;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Localization;
using Icons = Microsoft.FluentUI.AspNetCore.Components.Icons;

namespace Aspire.Dashboard.Components.Pages;

/// <summary>
/// Displays the overview of the resources selected in <see cref="ResourcesLayout"/>. The page picks the overview for
/// the selection: an app model resource, a parameter, a source that only sends telemetry, or a summary of several
/// resources.
/// </summary>
public sealed partial class Overview : ComponentBase, IDisposable
{
    private ResourcesLayout? _subscribedLayout;
    private bool _hideEmptyPanels = true;

    [Parameter]
    public string? ResourceName { get; set; }

    [CascadingParameter]
    public ResourcesLayout? ResourcesLayout { get; set; }

    [Inject]
    public required IDashboardClient DashboardClient { get; init; }

    [Inject]
    public required DashboardCommandExecutor DashboardCommandExecutor { get; init; }

    [Inject]
    public required IStringLocalizer<Dashboard.Resources.Resources> Loc { get; init; }

    private ResourceViewModel? Resource => ResourcesLayout?.SelectedResource;

    private ResourcesLayout.TelemetryOnlyResource? TelemetrySource =>
        Resource is null ? ResourcesLayout?.SelectedTelemetryOnlyResource : null;

    private IReadOnlyList<IconToggleOption<bool>> PanelOptions =>
    [
        new(true, new AspireIcons.Size20.HideEmptyPanels(), Loc[nameof(Dashboard.Resources.Resources.ResourceOverviewHideEmptyPanels)]),
        new(false, new Icons.Regular.Size20.Grid(), Loc[nameof(Dashboard.Resources.Resources.ResourceOverviewShowAllPanels)])
    ];

    protected override void OnParametersSet()
    {
        if (!ReferenceEquals(_subscribedLayout, ResourcesLayout))
        {
            _subscribedLayout?.ResourcesChanged -= OnResourcesChanged;
            _subscribedLayout = ResourcesLayout;
            _subscribedLayout?.ResourcesChanged += OnResourcesChanged;
        }
    }

    // A resource update can change which overview is displayed, for example when an AppHost resource arrives for a
    // selected source that previously only sent telemetry.
    private void OnResourcesChanged() => StateHasChanged();

    private Task SetHideEmptyPanelsAsync(bool value)
    {
        _hideEmptyPanels = value;
        return InvokeAsync(StateHasChanged);
    }

    private string GetResourceName(ResourceViewModel resource) => ResourcesLayout?.GetResourceName(resource) ?? resource.Name;

    private bool IsCommandExecuting(ResourceViewModel resource, CommandViewModel command) =>
        DashboardCommandExecutor.IsExecuting(resource.Name, command.Name);

    private Task ExecuteResourceCommandAsync(ResourceViewModel resource, CommandViewModel command)
        => DashboardCommandExecutor.ExecuteAsync(resource, command, GetResourceName);

    private Task ExecuteResourceCommandAsync(ResourceViewModel resource, CommandViewModel command, ExecuteResourceCommandOptions options)
        => DashboardCommandExecutor.ExecuteAsync(resource, command, GetResourceName, options);

    public void Dispose()
    {
        _subscribedLayout?.ResourcesChanged -= OnResourcesChanged;
    }
}
