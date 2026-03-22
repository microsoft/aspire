// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using System.Text.RegularExpressions;
using Aspire.Cli.Backchannel;
using Aspire.Cli.Mcp.Tools;
using Aspire.Cli.Tests.TestServices;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aspire.Cli.Tests.Mcp;

public class ListConsoleLogsToolTests
{
    [Fact]
    public async Task ListConsoleLogsTool_ThrowsException_WhenNoAppHostRunning()
    {
        var monitor = new TestAuxiliaryBackchannelMonitor();
        var tool = new ListConsoleLogsTool(monitor, NullLogger<ListConsoleLogsTool>.Instance);

        var arguments = new Dictionary<string, JsonElement>
        {
            ["resourceName"] = JsonDocument.Parse("\"test-resource\"").RootElement
        };

        var exception = await Assert.ThrowsAsync<ModelContextProtocol.McpProtocolException>(
            () => tool.CallToolAsync(CallToolContextTestHelper.Create(arguments), CancellationToken.None).AsTask()).DefaultTimeout();

        Assert.Contains("No Aspire AppHost", exception.Message);
        Assert.Contains("aspire start", exception.Message);
    }

    [Fact]
    public async Task ListConsoleLogsTool_ThrowsException_WhenResourceNameNotProvided()
    {
        var monitor = new TestAuxiliaryBackchannelMonitor();
        var connection = new TestAppHostAuxiliaryBackchannel();
        monitor.AddConnection("hash1", "socket.hash1", connection);

        var tool = new ListConsoleLogsTool(monitor, NullLogger<ListConsoleLogsTool>.Instance);

        var exception = await Assert.ThrowsAsync<ModelContextProtocol.McpProtocolException>(
            () => tool.CallToolAsync(CallToolContextTestHelper.Create(), CancellationToken.None).AsTask()).DefaultTimeout();

        Assert.Contains("resourceName", exception.Message);
    }

    [Fact]
    public async Task ListConsoleLogsTool_ReturnsLogs_WhenResourceHasNoLogs()
    {
        var monitor = new TestAuxiliaryBackchannelMonitor();
        var connection = new TestAppHostAuxiliaryBackchannel
        {
            LogLines = []
        };
        monitor.AddConnection("hash1", "socket.hash1", connection);

        var tool = new ListConsoleLogsTool(monitor, NullLogger<ListConsoleLogsTool>.Instance);

        var arguments = new Dictionary<string, JsonElement>
        {
            ["resourceName"] = JsonDocument.Parse("\"test-resource\"").RootElement
        };

        var result = await tool.CallToolAsync(CallToolContextTestHelper.Create(arguments), CancellationToken.None).DefaultTimeout();

        Assert.True(result.IsError is null or false);
        Assert.NotNull(result.Content);
        Assert.Single(result.Content);
        var textContent = result.Content[0] as ModelContextProtocol.Protocol.TextContentBlock;
        Assert.NotNull(textContent);

        var codeBlockContent = ExtractCodeBlockContent(textContent.Text);
        Assert.Equal("", codeBlockContent);
        Assert.StartsWith("Returned 0 console logs.", textContent.Text);
    }

    [Fact]
    public async Task ListConsoleLogsTool_ReturnsLogs_ForSpecificResource()
    {
        var monitor = new TestAuxiliaryBackchannelMonitor();
        var connection = new TestAppHostAuxiliaryBackchannel
        {
            LogLines =
            [
                new ResourceLogLine { ResourceName = "api-service", LineNumber = 1, Content = "Starting application...", IsError = false },
                new ResourceLogLine { ResourceName = "api-service", LineNumber = 2, Content = "Application started", IsError = false },
                new ResourceLogLine { ResourceName = "other-service", LineNumber = 1, Content = "Different service log", IsError = false }
            ]
        };
        monitor.AddConnection("hash1", "socket.hash1", connection);

        var tool = new ListConsoleLogsTool(monitor, NullLogger<ListConsoleLogsTool>.Instance);

        var arguments = new Dictionary<string, JsonElement>
        {
            ["resourceName"] = JsonDocument.Parse("\"api-service\"").RootElement
        };

        var result = await tool.CallToolAsync(CallToolContextTestHelper.Create(arguments), CancellationToken.None).DefaultTimeout();

        Assert.True(result.IsError is null or false);
        var textContent = result.Content![0] as ModelContextProtocol.Protocol.TextContentBlock;
        Assert.NotNull(textContent);

        var codeBlockContent = ExtractCodeBlockContent(textContent.Text);
        Assert.Equal(
            """
            Starting application...
            Application started
            """, codeBlockContent);
    }

    [Fact]
    public async Task ListConsoleLogsTool_ReturnsPlainTextFormat()
    {
        var monitor = new TestAuxiliaryBackchannelMonitor();
        var connection = new TestAppHostAuxiliaryBackchannel
        {
            LogLines =
            [
                new ResourceLogLine { ResourceName = "api-service", LineNumber = 1, Content = "Test log line", IsError = false }
            ]
        };
        monitor.AddConnection("hash1", "socket.hash1", connection);

        var tool = new ListConsoleLogsTool(monitor, NullLogger<ListConsoleLogsTool>.Instance);

        var arguments = new Dictionary<string, JsonElement>
        {
            ["resourceName"] = JsonDocument.Parse("\"api-service\"").RootElement
        };

        var result = await tool.CallToolAsync(CallToolContextTestHelper.Create(arguments), CancellationToken.None).DefaultTimeout();

        var textContent = result.Content![0] as ModelContextProtocol.Protocol.TextContentBlock;
        Assert.NotNull(textContent);

        var codeBlockContent = ExtractCodeBlockContent(textContent.Text);
        Assert.Equal("Test log line", codeBlockContent);
        Assert.StartsWith("Returned 1 console log.", textContent.Text);
    }

    [Fact]
    public async Task ListConsoleLogsTool_StripsTimestamps()
    {
        var monitor = new TestAuxiliaryBackchannelMonitor();
        var connection = new TestAppHostAuxiliaryBackchannel
        {
            LogLines =
            [
                new ResourceLogLine { ResourceName = "api-service", LineNumber = 1, Content = "2024-01-15T10:30:00.123Z Log message after timestamp", IsError = false }
            ]
        };
        monitor.AddConnection("hash1", "socket.hash1", connection);

        var tool = new ListConsoleLogsTool(monitor, NullLogger<ListConsoleLogsTool>.Instance);

        var arguments = new Dictionary<string, JsonElement>
        {
            ["resourceName"] = JsonDocument.Parse("\"api-service\"").RootElement
        };

        var result = await tool.CallToolAsync(CallToolContextTestHelper.Create(arguments), CancellationToken.None).DefaultTimeout();

        var textContent = result.Content![0] as ModelContextProtocol.Protocol.TextContentBlock;
        Assert.NotNull(textContent);

        var codeBlockContent = ExtractCodeBlockContent(textContent.Text);
        Assert.Equal("Log message after timestamp", codeBlockContent);
    }

    [Fact]
    public async Task ListConsoleLogsTool_StripsAnsiSequences()
    {
        var monitor = new TestAuxiliaryBackchannelMonitor();
        var connection = new TestAppHostAuxiliaryBackchannel
        {
            LogLines =
            [
                new ResourceLogLine { ResourceName = "api-service", LineNumber = 1, Content = "\u001b[32mGreen text\u001b[0m normal text", IsError = false }
            ]
        };
        monitor.AddConnection("hash1", "socket.hash1", connection);

        var tool = new ListConsoleLogsTool(monitor, NullLogger<ListConsoleLogsTool>.Instance);

        var arguments = new Dictionary<string, JsonElement>
        {
            ["resourceName"] = JsonDocument.Parse("\"api-service\"").RootElement
        };

        var result = await tool.CallToolAsync(CallToolContextTestHelper.Create(arguments), CancellationToken.None).DefaultTimeout();

        var textContent = result.Content![0] as ModelContextProtocol.Protocol.TextContentBlock;
        Assert.NotNull(textContent);

        var codeBlockContent = ExtractCodeBlockContent(textContent.Text);
        Assert.Equal("Green text normal text", codeBlockContent);
    }

    private static string ExtractCodeBlockContent(string text)
    {
        var match = Regex.Match(text, @"```plaintext\s*(.*?)\s*```", RegexOptions.Singleline);
        return match.Success ? match.Groups[1].Value : string.Empty;
    }
}

