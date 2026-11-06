// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.RegularExpressions;
using Aspire.Dashboard.Components.CustomIcons;
using Aspire.Dashboard.Utils;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Localization;
using Microsoft.JSInterop;
using Icons = Microsoft.FluentUI.AspNetCore.Components.Icons;

namespace Aspire.Dashboard.Components.Legacy.Layout;

public partial class LegacyMobileNavMenu : ComponentBase, IAsyncDisposable
{
    internal const string MobileNavMenuId = "dashboard-mobile-nav-menu";

    private IJSObjectReference? _keyboardNavigation;
    private DotNetObjectReference<LegacyMobileNavMenu>? _mobileNavMenuReference;
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
        await JS.InvokeVoidAsync("focusElement", LegacyMainLayout.NavigationButtonId);
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
                Loc[nameof(Resources.Layout.NavMenuResourcesTab)],
                () => NavigateToAsync(LegacyNavUrls.ToLegacyUrl(DashboardUrls.ResourcesUrl())),
                LegacyDesktopNavMenu.ResourcesIcon(),
                ActiveIcon: LegacyDesktopNavMenu.ResourcesIcon(active: true),
                LinkMatchRegex: s_resourcesPageRegex
            );

            yield return new MobileNavMenuEntry(
                Loc[nameof(Resources.Layout.NavMenuConsoleLogsTab)],
                () => NavigateToAsync(LegacyNavUrls.ToLegacyUrl(DashboardUrls.ConsoleLogsUrl())),
                LegacyDesktopNavMenu.ConsoleLogsIcon(),
                ActiveIcon: LegacyDesktopNavMenu.ConsoleLogsIcon(active: true),
                LinkMatchRegex: GetNonIndexPageRegex(LegacyNavUrls.ToLegacyUrl(DashboardUrls.ConsoleLogsUrl()))
            );

        }

        yield return new MobileNavMenuEntry(
            Loc[nameof(Resources.Layout.NavMenuStructuredLogsTab)],
            () => NavigateToAsync(LegacyNavUrls.ToLegacyUrl(DashboardUrls.StructuredLogsUrl())),
            LegacyDesktopNavMenu.StructuredLogsIcon(),
            ActiveIcon: LegacyDesktopNavMenu.StructuredLogsIcon(active: true),
            LinkMatchRegex: GetNonIndexPageRegex(LegacyNavUrls.ToLegacyUrl(DashboardUrls.StructuredLogsUrl()))
        );

        yield return new MobileNavMenuEntry(
            Loc[nameof(Resources.Layout.NavMenuTracesTab)],
            () => NavigateToAsync(LegacyNavUrls.ToLegacyUrl(DashboardUrls.TracesUrl())),
            LegacyDesktopNavMenu.TracesIcon(),
            ActiveIcon: LegacyDesktopNavMenu.TracesIcon(active: true),
            LinkMatchRegex: GetNonIndexPageRegex(LegacyNavUrls.ToLegacyUrl(DashboardUrls.TracesUrl()))
        );

        yield return new MobileNavMenuEntry(
            Loc[nameof(Resources.Layout.NavMenuMetricsTab)],
            () => NavigateToAsync(LegacyNavUrls.ToLegacyUrl(DashboardUrls.MetricsUrl())),
            LegacyDesktopNavMenu.MetricsIcon(),
            ActiveIcon: LegacyDesktopNavMenu.MetricsIcon(active: true),
            LinkMatchRegex: GetNonIndexPageRegex(LegacyNavUrls.ToLegacyUrl(DashboardUrls.MetricsUrl()))
        );

        if (DashboardClient.IsEnabled && HasResourceTerminals)
        {
            yield return new MobileNavMenuEntry(
                Loc[nameof(Resources.Layout.NavMenuTerminalsTab)],
                () => NavigateToAsync(LegacyNavUrls.ToLegacyUrl(DashboardUrls.TerminalsUrl())),
                LegacyDesktopNavMenu.TerminalsIcon(),
                ActiveIcon: LegacyDesktopNavMenu.TerminalsIcon(active: true),
                LinkMatchRegex: GetNonIndexPageRegex(LegacyNavUrls.ToLegacyUrl(DashboardUrls.TerminalsUrl()))
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

    private static Regex GetNonIndexPageRegex(string pageRelativeBasePath)
    {
        pageRelativeBasePath = Regex.Escape(pageRelativeBasePath);
        return new Regex($"^({pageRelativeBasePath}(\\?.*)?|{pageRelativeBasePath}/.+)$", LinkMatchRegexOptions);
    }

    // The resources page is /legacy, /legacy/ (where the layout sends "/") or /legacy/resources, with an optional query,
    // for example /legacy?resource=api.
    private static readonly Regex s_resourcesPageRegex = new($"^{LegacyNavUrls.BasePath}(/|/resources/?)?(\\?.*)?$", LinkMatchRegexOptions);

    private const RegexOptions LinkMatchRegexOptions = RegexOptions.CultureInvariant | RegexOptions.IgnoreCase;
}
