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
}
