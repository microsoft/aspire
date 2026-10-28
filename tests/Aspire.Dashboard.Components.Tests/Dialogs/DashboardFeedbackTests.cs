// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Components.Dialogs;
using Aspire.Dashboard.Components.Tests.Shared;
using Aspire.Dashboard.Model;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Microsoft.FluentUI.AspNetCore.Components;
using Xunit;

namespace Aspire.Dashboard.Components.Tests.Dialogs;

[UseCulture("en-US")]
public sealed class DashboardFeedbackTests : DashboardTestContext
{
    [Fact]
    public void BuildMarkdown_OmitsUnansweredSections()
    {
        FluentUISetupHelpers.AddCommonDashboardServices(this);
        var loc = Services.GetRequiredService<IStringLocalizer<Resources.Dialogs>>();
        var feedback = new DashboardFeedback { IsAppHost = false };

        var markdown = feedback.BuildMarkdown(loc);

        Assert.Equal(
            """
            ### Dashboard experience feedback

            **Dashboard mode**
            Standalone telemetry
            """,
            markdown);
    }

    [Fact]
    public void BuildMarkdown_IncludesOnlySelectedAndEnteredFeedback()
    {
        FluentUISetupHelpers.AddCommonDashboardServices(this);
        var loc = Services.GetRequiredService<IStringLocalizer<Resources.Dialogs>>();
        var feedback = new DashboardFeedback
        {
            IsAppHost = true,
            UsageDuration = DashboardFeedbackUsageDuration.TwoToSevenDays,
            MissingCapabilities = "A direct link to my frontend.",
            Difficulties = "Primary navigation was difficult to scan.",
            OtherReason = "The layout used too much horizontal space."
        };
        feedback.Reasons.Add(DashboardFeedbackReason.Navigation);
        feedback.Reasons.Add(DashboardFeedbackReason.Other);

        var markdown = feedback.BuildMarkdown(loc);

        Assert.Equal(
            """
            ### Dashboard experience feedback

            **Time using the new dashboard**
            2-7 days

            **Reasons for returning to the previous dashboard**
            - Navigation
            - Other: The layout used too much horizontal space.

            **What are you missing from the new dashboard?**
            A direct link to my frontend.

            **What felt harder or less clear?**
            Primary navigation was difficult to scan.

            **Dashboard mode**
            AppHost
            """,
            markdown);
    }

    [Fact]
    public async Task CopyAndOpenDiscussion_CopiesConditionalMarkdownAndOpensConfiguredDiscussion()
    {
        FluentUISetupHelpers.AddCommonDashboardServices(this);
        FluentUISetupHelpers.SetupFluentUIComponents(this);
        JSInterop.Setup<bool>("copyTextAndOpen", _ => true).SetResult(true);
        var feedback = new DashboardFeedback
        {
            IsAppHost = true,
            MissingCapabilities = "A missing workflow."
        };
        var cut = Render<DashboardFeedbackDialog>(builder => builder
            .Add(p => p.Content, feedback)
            .AddCascadingValue(CreateDialogInstance()));

        await cut.InvokeAsync(() => Assert.Single(
            cut.FindAll("fluent-button"),
            button => button.TextContent.Contains("Continue", StringComparison.Ordinal)).Click());
        var copyAndOpenButton = Assert.Single(
            cut.FindAll("fluent-button"),
            button => button.TextContent.Contains("Copy and open discussion", StringComparison.Ordinal));
        await cut.InvokeAsync(() => copyAndOpenButton.Click());

        var invocation = Assert.Single(JSInterop.Invocations["copyTextAndOpen"]);
        Assert.Contains("A missing workflow.", Assert.IsType<string>(invocation.Arguments[0]));
        Assert.Equal(DashboardFeedbackDialog.DiscussionUrl, invocation.Arguments[1]);
    }

    [Fact]
    public async Task CopyFailure_DoesNotPreventSwitchingToPreviousDashboard()
    {
        FluentUISetupHelpers.AddCommonDashboardServices(this);
        FluentUISetupHelpers.SetupFluentUIComponents(this);
        JSInterop.Setup<bool>("copyTextAndOpen", _ => true).SetResult(false);
        var switched = false;
        var feedback = new DashboardFeedback
        {
            IsAppHost = true,
            SwitchToLegacyAsync = () =>
            {
                switched = true;
                return Task.CompletedTask;
            }
        };
        var cut = Render<DashboardFeedbackDialog>(builder => builder
            .Add(p => p.Content, feedback)
            .AddCascadingValue(CreateDialogInstance()));

        await cut.InvokeAsync(() => Assert.Single(
            cut.FindAll("fluent-button"),
            button => button.TextContent.Contains("Continue", StringComparison.Ordinal)).Click());
        var copyAndOpenButton = Assert.Single(
            cut.FindAll("fluent-button"),
            button => button.TextContent.Contains("Open new discussion and switch", StringComparison.Ordinal));
        await cut.InvokeAsync(() => copyAndOpenButton.Click());

        Assert.True(switched);
    }

    private static IDialogInstance CreateDialogInstance()
    {
        var dialogService = new Aspire.Tests.Shared.TestDialogService();
        var options = new DialogOptions
        {
            Parameters = new Dictionary<string, object?>(),
            Data = new DialogParameters()
        };
        _ = dialogService.ShowDialogAsync(typeof(DashboardFeedbackDialog), options);
        return dialogService.LastInstance!;
    }
}
