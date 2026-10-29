// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Model;
using Aspire.Dashboard.Utils;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Routing;
using Microsoft.FluentUI.AspNetCore.Components;
using Icons = Microsoft.FluentUI.AspNetCore.Components.Icons;

namespace Aspire.Dashboard.Components.Layout;

/// <summary>
/// The top-level navigation displayed across the top of the desktop layout. With a resource
/// service the dashboard is organized around the resource selection and
/// telemetry is reached from the pages of a resource selection. Telemetry-only services have a separate section
/// in both AppHost and standalone mode.
/// </summary>
public partial class DesktopNavMenu : ComponentBase, IDisposable
{
    internal static Icon TelemetryIcon(bool active = false) =>
        active ? new Icons.Filled.Size24.DataUsage() : new Icons.Regular.Size24.DataUsage();

    internal static Icon HomeIcon(bool active = false) =>
        active ? new Icons.Filled.Size24.Home()
                  : new Icons.Regular.Size24.Home();

    internal static Icon ConsoleLogsIcon(bool active = false) =>
        active ? new Icons.Filled.Size24.SlideText()
                  : new Icons.Regular.Size24.SlideText();

    internal static Icon ResourcesIcon(bool active = false) =>
        active ? new Icons.Filled.Size24.AppFolder()
                  : new Icons.Regular.Size24.AppFolder();

    // WindowConsole only ships at Size20 in this Fluent version, so callers scale it to 24px. The Size24 alternatives
    // (WindowDevTools, Code) read as "developer tools" rather than "terminal".
    internal static Icon TerminalsIcon(bool active = false) =>
        active ? new Icons.Filled.Size20.WindowConsole()
                  : new Icons.Regular.Size20.WindowConsole();

    internal static Icon ExtensionsIcon(bool active = false) =>
        active ? new Icons.Filled.Size24.PuzzlePiece()
                  : new Icons.Regular.Size24.PuzzlePiece();

    internal static Icon ParametersIcon(bool active = false) =>
        active ? new Icons.Filled.Size24.Key()
                  : new Icons.Regular.Size24.Key();

    internal static Icon GraphIcon(bool active = false) =>
        active ? new Icons.Filled.Size24.ShareAndroid()
                  : new Icons.Regular.Size24.ShareAndroid();

    internal static Icon StructuredLogsIcon(bool active = false) =>
        active ? new Icons.Filled.Size24.SlideTextSparkle()
                  : new Icons.Regular.Size24.SlideTextSparkle();

    internal static Icon TracesIcon(bool active = false) =>
        active ? new Icons.Filled.Size24.GanttChart()
                  : new Icons.Regular.Size24.GanttChart();

    internal static Icon MetricsIcon(bool active = false) =>
        active ? new Icons.Filled.Size24.ChartMultiple()
                  : new Icons.Regular.Size24.ChartMultiple();

    [Inject]
    public required NavigationManager NavigationManager { get; init; }

    [Inject]
    public required ResourcePaneState ResourcePaneState { get; init; }

    private NavSection _activeSection;

    protected override void OnInitialized()
    {
        NavigationManager.LocationChanged += OnLocationChanged;
        ResourcePaneState.Changed += OnResourcePaneChanged;
        _activeSection = GetSection(NavigationManager.ToBaseRelativePath(NavigationManager.Uri), DashboardClient.IsEnabled);
    }

    private void OnResourcePaneChanged()
    {
        if (_activeSection == NavSection.Resources)
        {
            _ = InvokeAsync(StateHasChanged);
        }
    }

    // Telemetry-source pages share the resource routes, so the pane determines their navigation highlight.
    private NavSection GetDisplayedSection() =>
        _activeSection == NavSection.Resources && ResourcePaneState.Mode == ResourcePaneMode.Telemetry
            ? NavSection.Telemetry
            : _activeSection;

    private void OnLocationChanged(object? sender, LocationChangedEventArgs e)
    {
        // Always re-render: the links to a selection's pages follow the selection in the URL.
        _activeSection = GetSection(NavigationManager.ToBaseRelativePath(e.Location), DashboardClient.IsEnabled);
        _ = InvokeAsync(StateHasChanged);
    }

    /// <summary>
    /// Gets the URL of a page of the current resource selection, such as its console logs. The selection is read from
    /// the current URL; pages that aren't about resources, like the parameters, select every resource.
    /// </summary>
    internal static string GetSelectionPageUrl(NavigationManager navigationManager, ResourceTab tab)
    {
        var path = navigationManager.ToBaseRelativePath(navigationManager.Uri);
        if (tab == ResourceTab.Overview && ResourcesLayout.ParsePaneMode(path) == ResourcePaneMode.Telemetry)
        {
            return DashboardUrls.ResourceListUrl();
        }

        IReadOnlyList<string> resourceNames = [];
        string? routeResourceName = null;
        if (GetSection(path, hasResourceService: true) is NavSection.Resources or NavSection.Telemetry)
        {
            var location = ResourcesLayout.ParseLocation(path);
            resourceNames = location.SelectedResourceNames;
            routeResourceName = location.RouteResourceName;
        }

        // Without a selection, the overview restores the last selected resources, like the Resources entry always did.
        if (tab == ResourceTab.Overview && resourceNames.Count == 0)
        {
            return DashboardUrls.ResourceListUrl();
        }

        var url = ResourcesLayout.GetTabUrl(tab, resourceNames, routeResourceName);
        return ResourcesLayout.ParsePaneMode(path) == ResourcePaneMode.Telemetry && tab is not (ResourceTab.Overview or ResourceTab.Console)
            ? DashboardUrls.AddTelemetryPane(url)
            : url;
    }

    private IEnumerable<NavItem> GetItems()
    {
        if (DashboardClient.IsEnabled)
        {
            // The pages of a selection (overview, console logs, structured logs, traces and metrics) are icons at the
            // top of the resources view, so the app navigation has a single entry for them.
            yield return new NavItem(NavSection.Resources, GetSelectionPageUrl(NavigationManager, ResourceTab.Overview), Loc[nameof(Resources.Layout.NavMenuResourcesTab)], ResourcesIcon(), ResourcesIcon(active: true));
        }
        yield return new NavItem(NavSection.Telemetry, DashboardUrls.TelemetrySourcesUrl(), Loc[nameof(Resources.Layout.NavMenuTelemetrySourcesTab)], TelemetryIcon(), TelemetryIcon(active: true));
    }

    /// <summary>
    /// Maps a base-relative path (e.g. <c>traces/resource/api?type=http</c>) to the top-level section it belongs to.
    /// </summary>
    internal static NavSection GetSection(string baseRelativePath, bool hasResourceService)
    {
        if (ResourcesLayout.ParsePaneMode(baseRelativePath) == ResourcePaneMode.Telemetry)
        {
            return NavSection.Telemetry;
        }

        var path = baseRelativePath;
        var queryIndex = path.IndexOfAny(['?', '#']);
        if (queryIndex >= 0)
        {
            path = path[..queryIndex];
        }

        var firstSegment = path.Split('/', 2)[0];

        if (!hasResourceService)
        {
            return firstSegment switch
            {
                DashboardUrls.StructuredLogsBasePath or
                DashboardUrls.TracesBasePath or
                DashboardUrls.MetricsBasePath => NavSection.Telemetry,
                _ => NavSection.None
            };
        }

        return firstSegment switch
        {
            // The root URL redirects to the resources.
            "" => NavSection.Resources,
            // The pages of a selection are part of the resources view.
            DashboardUrls.ConsoleLogBasePath or
            DashboardUrls.StructuredLogsBasePath or
            DashboardUrls.TracesBasePath or
            DashboardUrls.MetricsBasePath => NavSection.Resources,
            DashboardUrls.ParametersBasePath => NavSection.Parameters,
            DashboardUrls.GraphBasePath => NavSection.Graph,
            DashboardUrls.TerminalsBasePath => path.StartsWith($"{DashboardUrls.TerminalsBasePath}/resource/", StringComparisons.UrlPath)
                ? NavSection.Resources
                : NavSection.Terminals,
            DashboardUrls.ExtensionsBasePath => NavSection.Extensions,
            DashboardUrls.ResourceOverviewBasePath => NavSection.Resources,
            _ => NavSection.None
        };
    }

    public void Dispose()
    {
        NavigationManager.LocationChanged -= OnLocationChanged;
        ResourcePaneState.Changed -= OnResourcePaneChanged;
    }

    internal enum NavSection
    {
        None,
        Resources,
        Telemetry,
        Parameters,
        Graph,
        Terminals,
        Extensions
    }

    private sealed record NavItem(NavSection Section, string Href, string Text, Icon Icon, Icon ActiveIcon);
}
