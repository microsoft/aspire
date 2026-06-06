// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Configuration;
using Aspire.Dashboard.Utils;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Routing;
using Microsoft.Extensions.Options;
using Microsoft.FluentUI.AspNetCore.Components;
using Icons = Microsoft.FluentUI.AspNetCore.Components.Icons;

namespace Aspire.Dashboard.Components.Layout;

public partial class DesktopNavMenu : ComponentBase, IDisposable
{
    internal static Icon ResourcesIcon(bool active = false) =>
        active ? new Icons.Filled.Size24.AppFolder()
                  : new Icons.Regular.Size24.AppFolder();

    internal static Icon GraphIcon(bool active = false) =>
        active ? new Icons.Filled.Size24.ShareAndroid()
                  : new Icons.Regular.Size24.ShareAndroid();

    internal static Icon ConsoleLogsIcon(bool active = false) =>
        active ? new Icons.Filled.Size24.SlideText()
                  : new Icons.Regular.Size24.SlideText();

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
    public required IOptionsMonitor<DashboardOptions> DashboardOptions { get; init; }

    protected bool IsResourceGraphEnabled => DashboardOptions.CurrentValue.UI.DisableResourceGraph != true;

    // NavLink has limited options for matching the current address when highlighting itself as active.
    // Can't use Match.All because of the query string. Can't use Match.Prefix always because it matches every page.
    // Track whether we are on the resource or graph page manually. If we are then change match to prefix to allow the query string.
    private bool _isResources;
    private bool _isGraph;

    protected override void OnInitialized()
    {
        NavigationManager.LocationChanged += OnLocationChanged;
        ProcessNavigationUri(NavigationManager.Uri);
    }

    private void OnLocationChanged(object? sender, LocationChangedEventArgs e)
    {
        ProcessNavigationUri(e.Location);
    }

    private void ProcessNavigationUri(string location)
    {
        if (Uri.TryCreate(location, UriKind.Absolute, out var result))
        {
            var trimmedPath = result.AbsolutePath.TrimStart('/');
            var isResources = trimmedPath == DashboardUrls.ResourcesBasePath || trimmedPath[0] == '?';
            var isGraph = string.Equals(trimmedPath.TrimEnd('/'), DashboardUrls.GraphBasePath, StringComparisons.UrlPath);
            if (isResources != _isResources || isGraph != _isGraph)
            {
                _isResources = isResources;
                _isGraph = isGraph;
                StateHasChanged();
            }
        }
    }

    public void Dispose()
    {
        NavigationManager.LocationChanged -= OnLocationChanged;
    }
}
