// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Components.Dialogs;
using Aspire.Dashboard.Components.Tests.Shared;
using Aspire.Dashboard.Configuration;
using Bunit;
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

        var links = cut.FindAll("a.help-link");

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

    private IRenderedComponent<HelpDialog> RenderHelpDialog(HelpDialogTab content = HelpDialogTab.KeyboardShortcuts, bool disableAgentHelp = false)
    {
        FluentUISetupHelpers.SetupDialogInfrastructure(this);
        FluentUISetupHelpers.SetupFluentTab(this);
        JSInterop.SetupModule("/Components/Controls/MarkdownRenderer.razor.js").SetupVoid();
        Services.AddOptions<DashboardOptions>().Configure(options =>
        {
            options.UI.DisableAgentHelp = disableAgentHelp;
            // The agents tab builds the dashboard's URL from its parsed endpoints.
            options.Frontend.EndpointUrls = "http://localhost:18888";
            Assert.True(options.Frontend.TryParseOptions(out _));
        });

        return Render<HelpDialog>(builder => builder.Add(p => p.Content, content));
    }
}
