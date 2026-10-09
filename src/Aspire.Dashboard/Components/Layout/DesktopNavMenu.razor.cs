// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Configuration;
using Aspire.Dashboard.Model;
using Aspire.Dashboard.Utils;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Routing;
using Microsoft.Extensions.Options;
using Microsoft.FluentUI.AspNetCore.Components;
using Icons = Microsoft.FluentUI.AspNetCore.Components.Icons;

namespace Aspire.Dashboard.Components.Layout;

/// <summary>
/// The top-level navigation displayed as a vertical icon rail on the left of the desktop layout. With a resource
/// service the dashboard is organized around the app model (Home, Resources, Tags, Parameters, Graph, Terminals,
/// Extensions) and telemetry is reached from a resource's tabs. Without a resource service there is no app model, so
/// the telemetry pages are the top-level sections.
/// </summary>
public partial class DesktopNavMenu : ComponentBase, IDisposable
{
    internal static Icon HomeIcon(bool active = false) =>
        active ? new Icons.Filled.Size24.Home()
                  : new Icons.Regular.Size24.Home();

    internal static Icon ResourcesIcon(bool active = false) =>
        active ? new Icons.Filled.Size24.AppFolder()
                  : new Icons.Regular.Size24.AppFolder();

    internal static Icon TagsIcon(bool active = false) =>
        active ? new Icons.Filled.Size24.TagMultiple()
                  : new Icons.Regular.Size24.TagMultiple();

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

    internal static Icon ConsoleLogsIcon(bool active = false) =>
        active ? new Icons.Filled.Size24.SlideText()
                  : new Icons.Regular.Size24.SlideText();

    [Parameter]
    public bool HasResourceTerminals { get; set; }

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

    // Resource pages share one route whichever way the resource list next to them is organized, so the URL alone
    // cannot tell Resources and Tags apart. The resource list's mode decides which of the two is current.
    private NavSection GetDisplayedSection() =>
        _activeSection == NavSection.Resources && ResourcePaneState.Mode == ResourcePaneMode.Tags
            ? NavSection.Tags
            : _activeSection;

    private void OnLocationChanged(object? sender, LocationChangedEventArgs e)
    {
        var section = GetSection(NavigationManager.ToBaseRelativePath(e.Location), DashboardClient.IsEnabled);
        if (section != _activeSection)
        {
            _activeSection = section;
            _ = InvokeAsync(StateHasChanged);
        }
    }

    private IEnumerable<NavItem> GetItems()
    {
        if (DashboardClient.IsEnabled)
        {
            yield return new NavItem(NavSection.Home, DashboardUrls.HomeUrl(), Loc[nameof(Resources.Layout.NavMenuHomeTab)], HomeIcon(), HomeIcon(active: true));
            yield return new NavItem(NavSection.Resources, DashboardUrls.ResourceListUrl(), Loc[nameof(Resources.Layout.NavMenuResourcesTab)], ResourcesIcon(), ResourcesIcon(active: true));
            yield return new NavItem(NavSection.Tags, DashboardUrls.TagsUrl(), Loc[nameof(Resources.Layout.NavMenuTagsTab)], TagsIcon(), TagsIcon(active: true));
            yield return new NavItem(NavSection.Parameters, DashboardUrls.ParametersUrl(), Loc[nameof(Resources.Layout.NavMenuParametersTab)], ParametersIcon(), ParametersIcon(active: true));
            if (DashboardOptions.CurrentValue.UI.DisableResourceGraph != true)
            {
                yield return new NavItem(NavSection.Graph, DashboardUrls.GraphUrl(), Loc[nameof(Resources.Layout.NavMenuGraphTab)], GraphIcon(), GraphIcon(active: true));
            }
            if (HasResourceTerminals)
            {
                yield return new NavItem(NavSection.Terminals, DashboardUrls.TerminalsUrl(), Loc[nameof(Resources.Layout.NavMenuTerminalsTab)], TerminalsIcon(), TerminalsIcon(active: true));
            }
            yield return new NavItem(NavSection.Extensions, DashboardUrls.ExtensionsUrl(), Loc[nameof(Resources.Layout.NavMenuExtensionsTab)], ExtensionsIcon(), ExtensionsIcon(active: true));
        }
        else
        {
            yield return new NavItem(NavSection.StructuredLogs, DashboardUrls.StructuredLogsUrl(), Loc[nameof(Resources.Layout.NavMenuStructuredLogsTab)], StructuredLogsIcon(), StructuredLogsIcon(active: true));
            yield return new NavItem(NavSection.Traces, DashboardUrls.TracesUrl(), Loc[nameof(Resources.Layout.NavMenuTracesTab)], TracesIcon(), TracesIcon(active: true));
            yield return new NavItem(NavSection.Metrics, DashboardUrls.MetricsUrl(), Loc[nameof(Resources.Layout.NavMenuMetricsTab)], MetricsIcon(), MetricsIcon(active: true));
        }
    }

    /// <summary>
    /// Maps a base-relative path (e.g. <c>traces/resource/api?type=http</c>) to the top-level section it belongs to.
    /// </summary>
    internal static NavSection GetSection(string baseRelativePath, bool hasResourceService)
    {
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
                DashboardUrls.StructuredLogsBasePath => NavSection.StructuredLogs,
                DashboardUrls.TracesBasePath => NavSection.Traces,
                DashboardUrls.MetricsBasePath => NavSection.Metrics,
                _ => NavSection.None
            };
        }

        return firstSegment switch
        {
            "" => NavSection.Home,
            DashboardUrls.ParametersBasePath => NavSection.Parameters,
            DashboardUrls.GraphBasePath => NavSection.Graph,
            DashboardUrls.TerminalsBasePath => NavSection.Terminals,
            DashboardUrls.ExtensionsBasePath => NavSection.Extensions,
            DashboardUrls.ResourceOverviewBasePath or
            DashboardUrls.ConsoleLogBasePath or
            DashboardUrls.StructuredLogsBasePath or
            DashboardUrls.TracesBasePath or
            DashboardUrls.MetricsBasePath => NavSection.Resources,
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
        Home,
        Resources,
        Tags,
        Parameters,
        Graph,
        Terminals,
        Extensions,
        StructuredLogs,
        Traces,
        Metrics
    }

    private sealed record NavItem(NavSection Section, string Href, string Text, Icon Icon, Icon ActiveIcon);
}
