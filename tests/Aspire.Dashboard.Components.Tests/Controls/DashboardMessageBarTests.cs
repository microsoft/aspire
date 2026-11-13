// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Components.Controls;
using Aspire.Dashboard.Components.Tests.Shared;
using Aspire.Dashboard.Model;
using Aspire.Dashboard.Model.Interaction;
using Aspire.Dashboard.Resources;
using Aspire.Dashboard.Tests;
using Aspire.Dashboard.Utils;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.FluentUI.AspNetCore.Components;
using Xunit;

namespace Aspire.Dashboard.Components.Tests.Controls;

public sealed class DashboardMessageBarTests : DashboardTestContext
{
    [Fact]
    public async Task MarkupMessage_CodeBlock_RenderedInsideMarkdownContainer()
    {
        // Arrange
        FluentUISetupHelpers.SetupFluentButton(this);
        FluentUISetupHelpers.AddCommonDashboardServices(this);

        var markdownProcessor = InteractionMarkdownHelper.CreateProcessor(new TestStringLocalizer<ControlsStrings>());
        var message = InteractionMarkdownHelper.ToHtml(markdownProcessor, """
            Docker desktop failed to start. Please start it manually.
            ```
            at Program.Main()
            ```
            """);

        var cut = Render(builder =>
        {
            builder.OpenComponent<FluentMessageBarProvider>(0);
            builder.AddComponentParameter(1, nameof(FluentMessageBarProvider.Section), DashboardUIHelpers.MessageBarSection);
            builder.CloseComponent();
        });

        // Act
        var messageBarService = Services.GetRequiredService<DashboardMessageBarService>();
        await cut.InvokeAsync(() => messageBarService.ShowAsync(
            new DashboardMessageBarContent
            {
                Title = "Container runtime unhealthy",
                Message = message,
                UseMarkupString = true
            },
            MessageBarIntent.Error,
            DashboardUIHelpers.MessageBarSection));

        // Assert
        cut.WaitForAssertion(() =>
        {
            var messageBar = Assert.Single(cut.FindComponents<DashboardMessageBar>());

            // Code block styles in markdown.css are scoped to .markdown-container. Without it the code title
            // and copy button render as unstyled blocks on their own lines.
            var messageElement = messageBar.Find(".dashboard-message-bar-message");
            Assert.Contains("markdown-container", messageElement.ClassList);
            Assert.NotNull(messageBar.Find(".markdown-container .code-block .code-title"));
            Assert.NotNull(messageBar.Find(".markdown-container .code-block .code-copy-button"));
        });
    }

    [Fact]
    public async Task TextMessage_NotRenderedInsideMarkdownContainer()
    {
        // Arrange
        FluentUISetupHelpers.SetupFluentButton(this);
        FluentUISetupHelpers.AddCommonDashboardServices(this);

        var cut = Render(builder =>
        {
            builder.OpenComponent<FluentMessageBarProvider>(0);
            builder.AddComponentParameter(1, nameof(FluentMessageBarProvider.Section), DashboardUIHelpers.MessageBarSection);
            builder.CloseComponent();
        });

        // Act
        var messageBarService = Services.GetRequiredService<DashboardMessageBarService>();
        await cut.InvokeAsync(() => messageBarService.ShowAsync(
            new DashboardMessageBarContent
            {
                Title = "Title",
                Message = "Plain text message"
            },
            MessageBarIntent.Info,
            DashboardUIHelpers.MessageBarSection));

        // Assert
        cut.WaitForAssertion(() =>
        {
            var messageBar = Assert.Single(cut.FindComponents<DashboardMessageBar>());
            var messageElement = messageBar.Find(".dashboard-message-bar-message");
            Assert.DoesNotContain("markdown-container", messageElement.ClassList);
            Assert.Equal("Plain text message", messageElement.TextContent.Trim());
        });
    }

    [Theory]
    [InlineData("https://example.com/feedback", "_blank")]
    [InlineData("/resources", null)]
    public async Task LinkAsAction_OpensExternalUrlsInNewTab(string linkUrl, string? expectedTarget)
    {
        FluentUISetupHelpers.SetupFluentButton(this);
        FluentUISetupHelpers.AddCommonDashboardServices(this);

        var cut = Render(builder =>
        {
            builder.OpenComponent<FluentMessageBarProvider>(0);
            builder.AddComponentParameter(1, nameof(FluentMessageBarProvider.Section), DashboardUIHelpers.MessageBarSection);
            builder.CloseComponent();
        });

        var messageBarService = Services.GetRequiredService<DashboardMessageBarService>();
        await cut.InvokeAsync(() => messageBarService.ShowAsync(
            new DashboardMessageBarContent
            {
                Message = "Message",
                LinkText = "Open",
                LinkUrl = linkUrl,
                LinkAsAction = true
            },
            MessageBarIntent.Info,
            DashboardUIHelpers.MessageBarSection));

        cut.WaitForAssertion(() =>
        {
            var link = Assert.Single(cut.FindComponents<DashboardMessageBar>()).Find(".dashboard-message-bar-actions a.dashboard-message-bar-link-action");
            Assert.Equal(linkUrl, link.GetAttribute("href"));
            Assert.Equal(expectedTarget, link.GetAttribute("target"));
            Assert.Equal(expectedTarget is null ? null : "noreferrer noopener", link.GetAttribute("rel"));
        });
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task LinkAsAction_CloseOnLinkClick_ClosesWithPrimaryResult(bool closeOnLinkClick)
    {
        FluentUISetupHelpers.SetupFluentButton(this);
        FluentUISetupHelpers.AddCommonDashboardServices(this);

        var cut = Render(builder =>
        {
            builder.OpenComponent<FluentMessageBarProvider>(0);
            builder.AddComponentParameter(1, nameof(FluentMessageBarProvider.Section), DashboardUIHelpers.MessageBarSection);
            builder.CloseComponent();
        });

        var closeResults = new List<object?>();
        var messageBarService = Services.GetRequiredService<DashboardMessageBarService>();
        await cut.InvokeAsync(() => messageBarService.ShowAsync(
            new DashboardMessageBarContent
            {
                Message = "Message",
                LinkText = "Open",
                LinkUrl = "https://example.com/feedback",
                LinkAsAction = true,
                CloseOnLinkClick = closeOnLinkClick
            },
            MessageBarIntent.Info,
            DashboardUIHelpers.MessageBarSection,
            result =>
            {
                closeResults.Add(result.Data);
                return Task.CompletedTask;
            }));

        cut.WaitForAssertion(() => Assert.Single(cut.FindComponents<DashboardMessageBar>()));
        cut.Find(".dashboard-message-bar-actions a.dashboard-message-bar-link-action").Click();

        if (closeOnLinkClick)
        {
            cut.WaitForAssertion(() =>
            {
                Assert.Equal([true], closeResults);
                Assert.Empty(cut.FindComponents<DashboardMessageBar>());
            });
        }
        else
        {
            Assert.Empty(closeResults);
            Assert.Single(cut.FindComponents<DashboardMessageBar>());
        }
    }
}
