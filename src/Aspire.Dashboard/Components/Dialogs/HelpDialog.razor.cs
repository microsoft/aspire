// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Components.CustomIcons;
using Aspire.Dashboard.Components.Layout;
using Aspire.Dashboard.Configuration;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Options;
using Microsoft.FluentUI.AspNetCore.Components;
using Icons = Microsoft.FluentUI.AspNetCore.Components.Icons;

namespace Aspire.Dashboard.Components.Dialogs;

/// <summary>
/// The tabs of the help dialog.
/// </summary>
public enum HelpDialogTab
{
    Documentation,
    KeyboardShortcuts,
    AIAgents
}

/// <summary>
/// The dashboard's help: documentation links, keyboard shortcuts and how AI agents use the dashboard, each in a tab.
/// </summary>
public partial class HelpDialog
{
    private const string TabIdPrefix = "help-tab-";
    private const string AspireDocumentationUrl = "https://aspire.dev/";
    private const string ReportIssueUrl = "https://github.com/microsoft/aspire/issues/new/choose";
    // The invite that aspire.dev links from its community page.
    private const string DiscordUrl = "https://discord.com/invite/raNPcaaSj8";

    private HelpDialogTab _activeTab;

    /// <summary>
    /// The tab to open on.
    /// </summary>
    [Parameter]
    public HelpDialogTab Content { get; set; } = HelpDialogTab.KeyboardShortcuts;

    [Inject]
    public required IStringLocalizer<Resources.Dialogs> Loc { get; init; }

    [Inject]
    public required IStringLocalizer<Resources.Layout> LayoutLoc { get; init; }

    [Inject]
    public required IOptionsMonitor<DashboardOptions> Options { get; init; }

    private bool IsAgentHelpEnabled => Options.CurrentValue.UI.DisableAgentHelp != true;

    protected override void OnInitialized()
    {
        // Agent help can be disabled, which removes its tab. Open on the documentation then, rather than on no tab.
        _activeTab = Content is HelpDialogTab.AIAgents && !IsAgentHelpEnabled ? HelpDialogTab.Documentation : Content;
    }

    private static string GetTabId(HelpDialogTab tab) => $"{TabIdPrefix}{tab}";

    private void OnTabChanged(FluentTab? tab)
    {
        if (tab?.Id is { } id
            && id.StartsWith(TabIdPrefix, StringComparison.Ordinal)
            && Enum.TryParse<HelpDialogTab>(id[TabIdPrefix.Length..], out var activeTab))
        {
            _activeTab = activeTab;
        }
    }

    private List<HelpLinkSection> GetLinkSections() =>
    [
        new("learn", Loc[nameof(Resources.Dialogs.HelpDialogLearnHeading)],
        [
            new(LayoutLoc[nameof(Resources.Layout.MainLayoutAspireDashboardHelpLink)],
                Loc[nameof(Resources.Dialogs.HelpDialogDashboardDocumentationDescription)],
                MainLayout.DashboardDocumentationUrl,
                new Icons.Regular.Size24.Book()),
            new(Loc[nameof(Resources.Dialogs.HelpDialogAspireDocumentationTitle)],
                Loc[nameof(Resources.Dialogs.HelpDialogAspireDocumentationDescription)],
                AspireDocumentationUrl,
                new Icons.Regular.Size24.Globe())
        ]),
        new("community", Loc[nameof(Resources.Dialogs.HelpDialogCommunityHeading)],
        [
            new(Loc[nameof(Resources.Dialogs.HelpDialogDiscordTitle)],
                Loc[nameof(Resources.Dialogs.HelpDialogDiscordDescription)],
                DiscordUrl,
                new AspireIcons.Size24.Discord()),
            new(LayoutLoc[nameof(Resources.Layout.MainLayoutAspireRepoLink)],
                Loc[nameof(Resources.Dialogs.HelpDialogRepoDescription)],
                MainLayout.AspireRepoUrl,
                new AspireIcons.Size24.GitHub()),
            new(Loc[nameof(Resources.Dialogs.HelpDialogReportIssueTitle)],
                Loc[nameof(Resources.Dialogs.HelpDialogReportIssueDescription)],
                ReportIssueUrl,
                new Icons.Regular.Size24.Bug()),
            new(Loc[nameof(Resources.Dialogs.HelpDialogDashboardFeedbackTitle)],
                Loc[nameof(Resources.Dialogs.HelpDialogDashboardFeedbackDescription)],
                DashboardFeedbackDialog.DiscussionUrl,
                new Icons.Regular.Size24.ChatHelp())
        ])
    ];

    private sealed record HelpLink(string Title, string Description, string Url, Icon Icon);

    private sealed record HelpLinkSection(string Id, string Heading, List<HelpLink> Links);
}
