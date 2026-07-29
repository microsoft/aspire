// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.RegularExpressions;
using Aspire.Dashboard.Components.Deck;
using Aspire.Dashboard.Utils;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Localization;
using Microsoft.JSInterop;

namespace Aspire.Dashboard.Components.Layout;

public partial class MobileNavMenu : ComponentBase
{
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

    private IEnumerable<MobileNavMenuEntry> GetMobileNavMenuEntries()
    {
        if (DashboardClient.IsEnabled)
        {
            yield return new MobileNavMenuEntry(
                Loc[nameof(Resources.Layout.NavMenuResourcesTab)],
                () => NavigateToAsync(DashboardUrls.ResourcesUrl()),
                DeckIconName.Resources,
                LinkMatchRegex: GetIndexPageRegex(DashboardUrls.ResourcesUrl())
            );

            yield return new MobileNavMenuEntry(
                Loc[nameof(Resources.Layout.NavMenuConsoleLogsTab)],
                () => NavigateToAsync(DashboardUrls.ConsoleLogsUrl()),
                DeckIconName.Console,
                LinkMatchRegex: GetNonIndexPageRegex(DashboardUrls.ConsoleLogsUrl())
            );
        }

        yield return new MobileNavMenuEntry(
            Loc[nameof(Resources.Layout.NavMenuStructuredLogsTab)],
            () => NavigateToAsync(DashboardUrls.StructuredLogsUrl()),
            DeckIconName.Logs,
            LinkMatchRegex: GetNonIndexPageRegex(DashboardUrls.StructuredLogsUrl())
        );

        yield return new MobileNavMenuEntry(
            Loc[nameof(Resources.Layout.NavMenuTracesTab)],
            () => NavigateToAsync(DashboardUrls.TracesUrl()),
            DeckIconName.Traces,
            LinkMatchRegex: GetNonIndexPageRegex(DashboardUrls.TracesUrl())
        );

        yield return new MobileNavMenuEntry(
            Loc[nameof(Resources.Layout.NavMenuMetricsTab)],
            () => NavigateToAsync(DashboardUrls.MetricsUrl()),
            DeckIconName.Metrics,
            LinkMatchRegex: GetNonIndexPageRegex(DashboardUrls.MetricsUrl())
        );

        yield return new MobileNavMenuEntry(
            Loc[nameof(Resources.Layout.MainLayoutAspireRepoLink)],
            async () =>
            {
                await JS.InvokeVoidAsync("open", ["https://aka.ms/aspire/repo", "_blank"]);
            },
            DeckIconName.GitHub
        );

        yield return new MobileNavMenuEntry(
            Loc[nameof(Resources.Layout.MainLayoutAspireDashboardHelpLink)],
            LaunchHelpAsync,
            DeckIconName.Help
        );

        if (IsAgentHelpEnabled)
        {
            yield return new MobileNavMenuEntry(
                Loc[nameof(Resources.Layout.MainLayoutLaunchAIAgents)],
                LaunchAIAgentsAsync,
                DeckIconName.Sparkle
            );
        }

        yield return new MobileNavMenuEntry(
            Loc[nameof(Resources.Layout.MainLayoutLaunchNotifications)],
            LaunchNotificationsAsync,
            DeckIconName.Bell
        );

        yield return new MobileNavMenuEntry(
            Loc[nameof(Resources.Layout.MainLayoutLaunchSettings)],
            LaunchSettingsAsync,
            DeckIconName.Settings
        );
    }

    private static Regex GetNonIndexPageRegex(string pageRelativeBasePath)
    {
        pageRelativeBasePath = Regex.Escape(pageRelativeBasePath);
        return new Regex($"^({pageRelativeBasePath}(\\?.*)?|{pageRelativeBasePath}/.+)$", LinkMatchRegexOptions);
    }

    private static Regex GetIndexPageRegex(string pageRelativeBasePath)
    {
        pageRelativeBasePath = Regex.Escape(pageRelativeBasePath);
        return new Regex($"^{pageRelativeBasePath}(\\?.*)?$", LinkMatchRegexOptions);
    }

    private const RegexOptions LinkMatchRegexOptions = RegexOptions.CultureInvariant | RegexOptions.IgnoreCase;
}
