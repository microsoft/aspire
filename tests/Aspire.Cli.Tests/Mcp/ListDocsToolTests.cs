// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Mcp.Tools;
using Aspire.Cli.Tests.TestServices;
using Microsoft.AspNetCore.InternalTesting;
using ModelContextProtocol.Protocol;

namespace Aspire.Cli.Tests.Mcp;

public class ListDocsToolTests
{
    [Fact]
    public async Task ListDocsTool_CallToolAsync_ReturnsDocumentList()
    {
        var indexService = new TestDocsIndexService();
        var tool = new ListDocsTool(indexService);

        var result = await tool.CallToolAsync(CallToolContextTestHelper.Create(), CancellationToken.None).DefaultTimeout();

        Assert.True(result.IsError is null or false);
        Assert.NotNull(result.Content);
        Assert.Single(result.Content);
        var textContent = result.Content[0] as TextContentBlock;
        Assert.NotNull(textContent);
        Assert.Contains("Aspire Documentation Pages", textContent.Text);
        Assert.Contains("Getting Started", textContent.Text);
        Assert.Contains("App Host", textContent.Text);
        Assert.Contains("Deploy to Azure", textContent.Text);
    }

    [Fact]
    public async Task ListDocsTool_CallToolAsync_SendsProgressNotifications_WhenIndexingRequired()
    {
        var indexService = new TestDocsIndexService(documents: null, isIndexed: false);
        var notifier = new TestMcpNotifier();
        var tool = new ListDocsTool(indexService);

        var context = CallToolContextTestHelper.Create(notifier: notifier, progressToken: "test-progress-token");
        var result = await tool.CallToolAsync(context, CancellationToken.None).DefaultTimeout();

        Assert.True(result.IsError is null or false);
        Assert.Contains(NotificationMethods.ProgressNotification, notifier.Notifications);
        // Should have two progress notifications: start and complete
        Assert.Equal(2, notifier.Notifications.Count(n => n == NotificationMethods.ProgressNotification));
    }

    [Fact]
    public async Task ListDocsTool_CallToolAsync_DoesNotSendProgressNotifications_WhenAlreadyIndexed()
    {
        var indexService = new TestDocsIndexService(); // IsIndexed = true by default
        var notifier = new TestMcpNotifier();
        var tool = new ListDocsTool(indexService);

        var context = CallToolContextTestHelper.Create(notifier: notifier, progressToken: "test-progress-token");
        var result = await tool.CallToolAsync(context, CancellationToken.None).DefaultTimeout();

        Assert.True(result.IsError is null or false);
        Assert.DoesNotContain(NotificationMethods.ProgressNotification, notifier.Notifications);
    }
}
