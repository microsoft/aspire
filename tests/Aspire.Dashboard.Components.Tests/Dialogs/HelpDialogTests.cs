// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Components.Dialogs;
using Aspire.Dashboard.Components.Tests.Shared;
using Aspire.Dashboard.Configuration;
using Aspire.Dashboard.Model;
using Aspire.Dashboard.Model.BrowserStorage;
using Aspire.Dashboard.Tests.Shared;
using Aspire.Dashboard.Utils;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.FluentUI.AspNetCore.Components;
using Xunit;

namespace Aspire.Dashboard.Components.Tests.Dialogs;

[UseCulture("en-US")]
public class HelpDialogTests : DashboardTestContext
{
    [Fact]
    public void Render_ShowsDocumentationShortcutsAndAgentsTabs()
    {
        var cut = RenderHelpDialog();

        Assert.Equal(
            [
                Resources.Dialogs.HelpDialogDocumentationTab,
                Resources.Layout.MainLayoutKeyboardShortcuts,
                Resources.Dialogs.AIAgentsDialogTitle
            ],
            cut.FindComponents<FluentTab>().Select(tab => tab.Instance.Header));
    }

    [Fact]
    public void Render_DocumentationTab_LinksOpenInNewTab()
    {
        var cut = RenderHelpDialog();

        var links = cut.FindAll("a.help-link[target='_blank']");

        Assert.Equal(
            [
                "https://aspire.dev/dashboard/overview/",
                "https://aspire.dev/",
                "https://discord.com/invite/raNPcaaSj8",
                "https://aka.ms/aspire/repo",
                "https://github.com/microsoft/aspire/issues/new/choose",
                DashboardFeedbackDialog.DiscussionUrl
            ],
            links.Select(link => link.GetAttribute("href")));
        Assert.All(links, link =>
        {
            Assert.Equal("_blank", link.GetAttribute("target"));
            Assert.Equal("noreferrer noopener", link.GetAttribute("rel"));
        });
    }

    [Theory]
    [InlineData(HelpDialogTab.Documentation)]
    [InlineData(HelpDialogTab.KeyboardShortcuts)]
    [InlineData(HelpDialogTab.AIAgents)]
    public void Render_OpensOnRequestedTab(HelpDialogTab tab)
    {
        var cut = RenderHelpDialog(content: tab);

        Assert.Equal($"help-tab-{tab}", cut.FindComponent<FluentTabs>().Instance.ActiveTabId);
    }

    [Fact]
    public void Render_AgentHelpDisabled_HidesAgentsTabAndOpensOnDocumentation()
    {
        var cut = RenderHelpDialog(content: HelpDialogTab.AIAgents, disableAgentHelp: true);

        Assert.Equal(
            [Resources.Dialogs.HelpDialogDocumentationTab, Resources.Layout.MainLayoutKeyboardShortcuts],
            cut.FindComponents<FluentTab>().Select(tab => tab.Instance.Header));
        Assert.Equal($"help-tab-{HelpDialogTab.Documentation}", cut.FindComponent<FluentTabs>().Instance.ActiveTabId);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Render_ShortcutsListGameKeyWhereTheGraphIsAvailable(bool graphAvailable)
    {
        var cut = RenderHelpDialog(subscribedShortcuts: graphAvailable ? [AspireKeyboardShortcut.GoToGraph] : []);

        var shortcut = cut.FindAll(".help-shortcut").SingleOrDefault(s => s.QuerySelector("dt")!.TextContent == Resources.Dialogs.HelpDialogPlayGame);

        Assert.Equal(graphAvailable, shortcut is not null);
        if (shortcut is not null)
        {
            Assert.Equal("o", shortcut.QuerySelector("kbd")!.TextContent);
        }
    }

    [Fact]
    public void Render_GameAvailable_ShowsCommunityLinkInsideTheDashboard()
    {
        var cut = RenderHelpDialog(content: HelpDialogTab.Documentation);

        var link = cut.Find("section[aria-labelledby='help-links-community'] a.help-link[href='/graph']");
        Assert.Equal(Resources.Dialogs.HelpDialogGameTitle, link.QuerySelector(".help-link-title")!.TextContent);
        Assert.Equal(Resources.Dialogs.HelpDialogGameDescription, link.QuerySelector(".help-link-description")!.TextContent);
        Assert.Null(link.GetAttribute("target"));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void Render_GameUnavailable_HidesGameLink(bool disableResourceGraph, bool disableResourceService)
    {
        var cut = RenderHelpDialog(content: HelpDialogTab.Documentation, disableResourceGraph: disableResourceGraph, isDashboardClientEnabled: !disableResourceService);

        Assert.Empty(cut.FindAll("a.help-link[href='/graph']"));
    }

    [Fact]
    public async Task ClickGameLink_SwitchesToGraphOfAllResourcesAndNavigates()
    {
        // The test storage doesn't keep values, so capture what's written to it.
        List<string>? lastSelected = null;
        var sessionStorage = new TestSessionStorage
        {
            OnSetAsync = (key, value) =>
            {
                if (key == BrowserStorageKeys.LastSelectedResources)
                {
                    lastSelected = (List<string>?)value;
                }
            }
        };
        var cut = RenderHelpDialog(content: HelpDialogTab.Documentation, sessionStorage: sessionStorage);

        await cut.Find("a.help-link[href='/graph']").ClickAsync(new MouseEventArgs());

        Assert.Equal(GroupOverview.Graph, Services.GetRequiredService<DashboardViewPreferences>().GroupOverview);
        Assert.NotNull(lastSelected);
        Assert.Empty(lastSelected);
        Assert.Equal("/resources", new Uri(Services.GetRequiredService<NavigationManager>().Uri).AbsolutePath);
    }

    private IRenderedComponent<HelpDialog> RenderHelpDialog(HelpDialogTab content = HelpDialogTab.KeyboardShortcuts, bool disableAgentHelp = false, bool disableResourceGraph = false, bool isDashboardClientEnabled = true, TestSessionStorage? sessionStorage = null, AspireKeyboardShortcut[]? subscribedShortcuts = null)
    {
        FluentUISetupHelpers.SetupDialogInfrastructure(this);
        FluentUISetupHelpers.SetupFluentTab(this);
        JSInterop.SetupModule("/Components/Controls/MarkdownRenderer.razor.js").SetupVoid();
        Services.AddSingleton<IDashboardClient>(new TestDashboardClient(isEnabled: isDashboardClientEnabled));
        Services.AddSingleton<ISessionStorage>(sessionStorage ?? new TestSessionStorage());
        Services.AddSingleton<ILocalStorage>(new TestLocalStorage());
        Services.AddScoped<DashboardViewPreferences>();
        Services.AddOptions<DashboardOptions>().Configure(options =>
        {
            options.UI.DisableAgentHelp = disableAgentHelp;
            options.UI.DisableResourceGraph = disableResourceGraph;
            // The agents tab builds the dashboard's URL from its parsed endpoints.
            options.Frontend.EndpointUrls = "http://localhost:18888";
            Assert.True(options.Frontend.TryParseOptions(out _));
        });

        // The layout is what subscribes the shortcuts the help tab lists.
        Services.GetRequiredService<ShortcutManager>().AddGlobalKeydownListener(new TestGlobalKeydownListener(subscribedShortcuts ?? []));

        return Render<HelpDialog>(builder => builder.Add(p => p.Content, content));
    }
}
