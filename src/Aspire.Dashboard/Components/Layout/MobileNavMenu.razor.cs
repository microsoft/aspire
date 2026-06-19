// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.RegularExpressions;
using Aspire.Dashboard.Components.CustomIcons;
using Aspire.Dashboard.Utils;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Localization;
using Microsoft.JSInterop;
using Icons = Microsoft.FluentUI.AspNetCore.Components.Icons;

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
    public required IStringLocalizer<Resources.AIAssistant> AIAssistantLoc { get; init; }

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
                DesktopNavMenu.ResourcesIcon(),
                ActiveIcon: DesktopNavMenu.ResourcesIcon(active: true),
                LinkMatchRegex: GetIndexPageRegex(DashboardUrls.ResourcesUrl())
            );

            yield return new MobileNavMenuEntry(
                Loc[nameof(Resources.Layout.NavMenuConsoleLogsTab)],
                () => NavigateToAsync(DashboardUrls.ConsoleLogsUrl()),
                DesktopNavMenu.ConsoleLogsIcon(),
                ActiveIcon: DesktopNavMenu.ConsoleLogsIcon(active: true),
                LinkMatchRegex: GetNonIndexPageRegex(DashboardUrls.ConsoleLogsUrl())
            );
        }

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

        if (IsAIEnabled)
        {
            yield return new MobileNavMenuEntry(
                AIAssistantLoc[nameof(Resources.AIAssistant.AIAssistantLaunchButtonText)],
                LaunchAIAssistantAsync,
                new AspireIcons.Size24.GitHubCopilot()
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

    private static Regex GetIndexPageRegex(string pageRelativeBasePath)
    {
        pageRelativeBasePath = Regex.Escape(pageRelativeBasePath);
        return new Regex($"^{pageRelativeBasePath}(\\?.*)?$", LinkMatchRegexOptions);
    }

    private const RegexOptions LinkMatchRegexOptions = RegexOptions.CultureInvariant | RegexOptions.IgnoreCase;
}
