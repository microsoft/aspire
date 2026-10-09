// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.RegularExpressions;
using Aspire.Dashboard.Components.CustomIcons;
using Aspire.Dashboard.Configuration;
using Aspire.Dashboard.Utils;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Options;
using Microsoft.JSInterop;
using Icons = Microsoft.FluentUI.AspNetCore.Components.Icons;

namespace Aspire.Dashboard.Components.Layout;

public partial class MobileNavMenu : ComponentBase, IAsyncDisposable
{
    internal const string MobileNavMenuId = "dashboard-mobile-nav-menu";

    private IJSObjectReference? _keyboardNavigation;
    private DotNetObjectReference<MobileNavMenu>? _mobileNavMenuReference;
    private bool _keyboardNavigationInitializing;
    private bool _disposed;

    [Parameter, EditorRequired]
    public required bool IsNavMenuOpen { get; set; }

    [Parameter]
    public bool HasResourceTerminals { get; set; }

    [Parameter, EditorRequired]
    public required Action CloseNavMenu { get; set; }

    [Parameter, EditorRequired]
    public required Func<Task> LaunchHelpAsync { get; set; }

    [Parameter, EditorRequired]
    public required Func<Task> LaunchAIAgentsAsync { get; set; }

    [Parameter, EditorRequired]
    public required bool IsAgentHelpEnabled { get; set; }

    [Parameter, EditorRequired]
    public required Func<Task> LaunchNotificationsAsync { get; set; }

    [Parameter, EditorRequired]
    public required Func<Task> LaunchSettingsAsync { get; set; }

    [Inject]
    public required NavigationManager NavigationManager { get; init; }

    [Inject]
    public required IDashboardClient DashboardClient { get; init; }

    [Inject]
    public required IOptionsMonitor<DashboardOptions> DashboardOptions { get; init; }

    [Inject]
    public required IStringLocalizer<Resources.Layout> Loc { get; init; }

    [Inject]
    public required IJSRuntime JS { get; init; }

    private Task NavigateToAsync(string url)
    {
        NavigationManager.NavigateTo(url);
        return Task.CompletedTask;
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!_disposed && IsNavMenuOpen && _keyboardNavigation is null && !_keyboardNavigationInitializing)
        {
            _keyboardNavigationInitializing = true;
            try
            {
                _mobileNavMenuReference ??= DotNetObjectReference.Create(this);
                var keyboardNavigation = await JS.InvokeAsync<IJSObjectReference>("initializeMobileNavMenuKeyboardNavigation", _mobileNavMenuReference, MobileNavMenuId);
                if (_disposed || !IsNavMenuOpen)
                {
                    await DisposeKeyboardNavigationAsync(keyboardNavigation);
                }
                else
                {
                    _keyboardNavigation = keyboardNavigation;
                }
            }
            finally
            {
                _keyboardNavigationInitializing = false;
            }
        }
        else if (!IsNavMenuOpen && _keyboardNavigation is not null)
        {
            await DisposeKeyboardNavigationAsync();
        }
    }

    public async ValueTask DisposeAsync()
    {
        _disposed = true;
        await DisposeKeyboardNavigationAsync();
        _mobileNavMenuReference?.Dispose();
    }

    [JSInvokable]
    public async Task CloseMobileNavMenuFromKeyboardAsync()
    {
        CloseNavMenu();
        await JS.InvokeVoidAsync("focusElement", MainLayout.NavigationButtonId);
    }

    [JSInvokable]
    public Task CloseMobileNavMenuFromFocusLossAsync()
    {
        CloseNavMenu();
        return Task.CompletedTask;
    }

    private async ValueTask DisposeKeyboardNavigationAsync()
    {
        if (_keyboardNavigation is { } keyboardNavigation)
        {
            _keyboardNavigation = null;
            await DisposeKeyboardNavigationAsync(keyboardNavigation);
        }
    }

    private async ValueTask DisposeKeyboardNavigationAsync(IJSObjectReference keyboardNavigation)
    {
        try
        {
            await JS.InvokeVoidAsync("disposeMobileNavMenuKeyboardNavigation", keyboardNavigation);
        }
        catch (JSDisconnectedException)
        {
            // The Blazor circuit can disconnect while the layout is being disposed.
            // In that case the browser listener is already gone with the page.
        }

        await JSInteropHelpers.SafeDisposeAsync(keyboardNavigation);
    }

    private IEnumerable<MobileNavMenuEntry> GetMobileNavMenuEntries()
    {
        if (DashboardClient.IsEnabled)
        {
            yield return new MobileNavMenuEntry(
                Loc[nameof(Resources.Layout.NavMenuHomeTab)],
                () => NavigateToAsync(DashboardUrls.HomeUrl()),
                DesktopNavMenu.HomeIcon(),
                ActiveIcon: DesktopNavMenu.HomeIcon(active: true),
                LinkMatchRegex: GetIndexPageRegex(DashboardUrls.HomeUrl())
            );

            // Telemetry pages are tabs of the Resources view, so they highlight the Resources entry.
            yield return new MobileNavMenuEntry(
                Loc[nameof(Resources.Layout.NavMenuResourcesTab)],
                () => NavigateToAsync(DashboardUrls.ResourceOverviewUrl()),
                DesktopNavMenu.ResourcesIcon(),
                ActiveIcon: DesktopNavMenu.ResourcesIcon(active: true),
                LinkMatchRegex: GetNonIndexPageRegex(
                    DashboardUrls.ResourceOverviewUrl(),
                    DashboardUrls.ConsoleLogsUrl(),
                    DashboardUrls.StructuredLogsUrl(),
                    DashboardUrls.TracesUrl(),
                    DashboardUrls.MetricsUrl())
            );

            // Tags is a view of the same resource pane, so it shares the Resources URL space and
            // doesn't get its own active highlight.
            yield return new MobileNavMenuEntry(
                Loc[nameof(Resources.Layout.NavMenuTagsTab)],
                () => NavigateToAsync(DashboardUrls.TagsUrl()),
                DesktopNavMenu.TagsIcon(),
                ActiveIcon: DesktopNavMenu.TagsIcon(active: true)
            );

            yield return new MobileNavMenuEntry(
                Loc[nameof(Resources.Layout.NavMenuParametersTab)],
                () => NavigateToAsync(DashboardUrls.ParametersUrl()),
                DesktopNavMenu.ParametersIcon(),
                ActiveIcon: DesktopNavMenu.ParametersIcon(active: true),
                LinkMatchRegex: GetNonIndexPageRegex(DashboardUrls.ParametersUrl())
            );

            if (DashboardOptions.CurrentValue.UI.DisableResourceGraph != true)
            {
                yield return new MobileNavMenuEntry(
                    Loc[nameof(Resources.Layout.NavMenuGraphTab)],
                    () => NavigateToAsync(DashboardUrls.GraphUrl()),
                    DesktopNavMenu.GraphIcon(),
                    ActiveIcon: DesktopNavMenu.GraphIcon(active: true),
                    LinkMatchRegex: GetNonIndexPageRegex(DashboardUrls.GraphUrl())
                );
            }

            if (HasResourceTerminals)
            {
                yield return new MobileNavMenuEntry(
                    Loc[nameof(Resources.Layout.NavMenuTerminalsTab)],
                    () => NavigateToAsync(DashboardUrls.TerminalsUrl()),
                    DesktopNavMenu.TerminalsIcon(),
                    ActiveIcon: DesktopNavMenu.TerminalsIcon(active: true),
                    LinkMatchRegex: GetNonIndexPageRegex(DashboardUrls.TerminalsUrl())
                );
            }

            yield return new MobileNavMenuEntry(
                Loc[nameof(Resources.Layout.NavMenuExtensionsTab)],
                () => NavigateToAsync(DashboardUrls.ExtensionsUrl()),
                DesktopNavMenu.ExtensionsIcon(),
                ActiveIcon: DesktopNavMenu.ExtensionsIcon(active: true),
                LinkMatchRegex: GetNonIndexPageRegex(DashboardUrls.ExtensionsUrl())
            );

        }
        else
        {
            yield return new MobileNavMenuEntry(
                Loc[nameof(Resources.Layout.NavMenuStructuredLogsTab)],
                () => NavigateToAsync(DashboardUrls.StructuredLogsUrl()),
                DesktopNavMenu.StructuredLogsIcon(),
                ActiveIcon: DesktopNavMenu.StructuredLogsIcon(active: true),
                LinkMatchRegex: GetNonIndexPageRegex(DashboardUrls.StructuredLogsUrl())
            );

            yield return new MobileNavMenuEntry(
                Loc[nameof(Resources.Layout.NavMenuTracesTab)],
                () => NavigateToAsync(DashboardUrls.TracesUrl()),
                DesktopNavMenu.TracesIcon(),
                ActiveIcon: DesktopNavMenu.TracesIcon(active: true),
                LinkMatchRegex: GetNonIndexPageRegex(DashboardUrls.TracesUrl())
            );

            yield return new MobileNavMenuEntry(
                Loc[nameof(Resources.Layout.NavMenuMetricsTab)],
                () => NavigateToAsync(DashboardUrls.MetricsUrl()),
                DesktopNavMenu.MetricsIcon(),
                ActiveIcon: DesktopNavMenu.MetricsIcon(active: true),
                LinkMatchRegex: GetNonIndexPageRegex(DashboardUrls.MetricsUrl())
            );
        }

        yield return new MobileNavMenuEntry(
            Loc[nameof(Resources.Layout.MainLayoutAspireRepoLink)],
            async () =>
            {
                await JS.InvokeVoidAsync("open", ["https://aka.ms/aspire/repo", "_blank"]);
            },
            new AspireIcons.Size24.GitHub()
        );

        yield return new MobileNavMenuEntry(
            Loc[nameof(Resources.Layout.MainLayoutAspireDashboardHelpLink)],
            LaunchHelpAsync,
            new Icons.Regular.Size24.QuestionCircle()
        );

        if (IsAgentHelpEnabled)
        {
            yield return new MobileNavMenuEntry(
                Loc[nameof(Resources.Layout.MainLayoutLaunchAIAgents)],
                LaunchAIAgentsAsync,
                new Icons.Regular.Size24.BotSparkle()
            );
        }

        yield return new MobileNavMenuEntry(
            Loc[nameof(Resources.Layout.MainLayoutLaunchNotifications)],
            LaunchNotificationsAsync,
            new Icons.Regular.Size24.Alert()
        );

        yield return new MobileNavMenuEntry(
            Loc[nameof(Resources.Layout.MainLayoutLaunchSettings)],
            LaunchSettingsAsync,
            new Icons.Regular.Size24.Settings()
        );
    }

    private static Regex GetNonIndexPageRegex(params string[] pageRelativeBasePaths)
    {
        var alternatives = pageRelativeBasePaths
            .Select(Regex.Escape)
            .Select(path => $"{path}(\\?.*)?|{path}/.+");
        return new Regex($"^({string.Join("|", alternatives)})$", LinkMatchRegexOptions);
    }

    private static Regex GetIndexPageRegex(string pageRelativeBasePath)
    {
        pageRelativeBasePath = Regex.Escape(pageRelativeBasePath);
        return new Regex($"^{pageRelativeBasePath}(\\?.*)?$", LinkMatchRegexOptions);
    }

    private const RegexOptions LinkMatchRegexOptions = RegexOptions.CultureInvariant | RegexOptions.IgnoreCase;
}
