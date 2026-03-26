// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using Aspire.Cli.Backchannel;
using Aspire.Cli.Mcp.Tools;
using Aspire.Cli.Tests.TestServices;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aspire.Cli.Tests.Mcp;

public class ExecuteResourceCommandToolTests
{
    private static IReadOnlyDictionary<string, JsonElement> CreateArguments(string resourceName, string commandName)
    {
        var doc = JsonDocument.Parse($$"""
            {
                "resourceName": "{{resourceName}}",
                "commandName": "{{commandName}}"
            }
            """);
        return doc.RootElement.EnumerateObject()
            .ToDictionary(p => p.Name, p => p.Value.Clone());
    }

    [Fact]
    public async Task ExecuteResourceCommandTool_ThrowsException_WhenNoAppHostRunning()
    {
        var monitor = new TestAuxiliaryBackchannelMonitor();
        var tool = new ExecuteResourceCommandTool(monitor, NullLogger<ExecuteResourceCommandTool>.Instance);

        var exception = await Assert.ThrowsAsync<ModelContextProtocol.McpProtocolException>(
            () => tool.CallToolAsync(CallToolContextTestHelper.Create(CreateArguments("test-resource", "resource-start")), CancellationToken.None).AsTask()).DefaultTimeout();

        Assert.Contains("No Aspire AppHost", exception.Message);
        Assert.Contains("--detach", exception.Message);
    }

    [Fact]
    public async Task ExecuteResourceCommandTool_ReturnsSuccess_WhenCommandExecutedSuccessfully()
    {
        var monitor = new TestAuxiliaryBackchannelMonitor();
        var connection = new TestAppHostAuxiliaryBackchannel
        {
            ExecuteResourceCommandResult = new ExecuteResourceCommandResponse { Success = true }
        };
        monitor.AddConnection("hash1", "socket.hash1", connection);

        var tool = new ExecuteResourceCommandTool(monitor, NullLogger<ExecuteResourceCommandTool>.Instance);
        var result = await tool.CallToolAsync(CallToolContextTestHelper.Create(CreateArguments("api-service", "resource-start")), CancellationToken.None).DefaultTimeout();

        Assert.True(result.IsError is null or false);
        Assert.NotNull(result.Content);
        Assert.Single(result.Content);
        var textContent = result.Content[0] as ModelContextProtocol.Protocol.TextContentBlock;
        Assert.NotNull(textContent);
        Assert.Contains("successfully", textContent.Text);
        Assert.Contains("api-service", textContent.Text);
        Assert.Contains("resource-start", textContent.Text);
    }

    [Fact]
    public async Task ExecuteResourceCommandTool_ThrowsException_WhenCommandFails()
    {
        var monitor = new TestAuxiliaryBackchannelMonitor();
        var connection = new TestAppHostAuxiliaryBackchannel
        {
            ExecuteResourceCommandResult = new ExecuteResourceCommandResponse
            {
                Success = false,
                ErrorMessage = "Resource not found"
            }
        };
        monitor.AddConnection("hash1", "socket.hash1", connection);

        var tool = new ExecuteResourceCommandTool(monitor, NullLogger<ExecuteResourceCommandTool>.Instance);

        var exception = await Assert.ThrowsAsync<ModelContextProtocol.McpProtocolException>(
            () => tool.CallToolAsync(CallToolContextTestHelper.Create(CreateArguments("nonexistent", "resource-start")), CancellationToken.None).AsTask()).DefaultTimeout();

        Assert.Contains("Resource not found", exception.Message);
    }

    [Fact]
    public async Task ExecuteResourceCommandTool_ThrowsException_WhenCommandCanceled()
    {
        var monitor = new TestAuxiliaryBackchannelMonitor();
        var connection = new TestAppHostAuxiliaryBackchannel
        {
            ExecuteResourceCommandResult = new ExecuteResourceCommandResponse
            {
                Success = false,
                Canceled = true
            }
        };
        monitor.AddConnection("hash1", "socket.hash1", connection);

        var tool = new ExecuteResourceCommandTool(monitor, NullLogger<ExecuteResourceCommandTool>.Instance);

        var exception = await Assert.ThrowsAsync<ModelContextProtocol.McpProtocolException>(
            () => tool.CallToolAsync(CallToolContextTestHelper.Create(CreateArguments("api-service", "resource-stop")), CancellationToken.None).AsTask()).DefaultTimeout();

        Assert.Contains("cancelled", exception.Message);
    }

    [Fact]
    public async Task ExecuteResourceCommandTool_WorksWithKnownCommands()
    {
        var monitor = new TestAuxiliaryBackchannelMonitor();
        var connection = new TestAppHostAuxiliaryBackchannel
        {
            ExecuteResourceCommandResult = new ExecuteResourceCommandResponse { Success = true }
        };
        monitor.AddConnection("hash1", "socket.hash1", connection);

        var tool = new ExecuteResourceCommandTool(monitor, NullLogger<ExecuteResourceCommandTool>.Instance);

        // Test with resource-start
        var startResult = await tool.CallToolAsync(CallToolContextTestHelper.Create(CreateArguments("api-service", "resource-start")), CancellationToken.None).DefaultTimeout();
        Assert.True(startResult.IsError is null or false);

        // Test with resource-stop
        var stopResult = await tool.CallToolAsync(CallToolContextTestHelper.Create(CreateArguments("api-service", "resource-stop")), CancellationToken.None).DefaultTimeout();
        Assert.True(stopResult.IsError is null or false);

        // Test with resource-restart
        var restartResult = await tool.CallToolAsync(CallToolContextTestHelper.Create(CreateArguments("api-service", "resource-restart")), CancellationToken.None).DefaultTimeout();
        Assert.True(restartResult.IsError is null or false);
    }

    [Fact]
    public async Task ExecuteResourceCommandTool_ThrowsException_WhenMissingArguments()
    {
        var monitor = new TestAuxiliaryBackchannelMonitor();
        var connection = new TestAppHostAuxiliaryBackchannel();
        monitor.AddConnection("hash1", "socket.hash1", connection);

        var tool = new ExecuteResourceCommandTool(monitor, NullLogger<ExecuteResourceCommandTool>.Instance);

        // Test with null arguments
        var exception1 = await Assert.ThrowsAsync<ModelContextProtocol.McpProtocolException>(
            () => tool.CallToolAsync(CallToolContextTestHelper.Create(), CancellationToken.None).AsTask()).DefaultTimeout();
        Assert.Contains("Missing required arguments", exception1.Message);

        // Test with only resourceName
        var partialArgs = JsonDocument.Parse("""{"resourceName": "test"}""").RootElement
            .EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());
        var exception2 = await Assert.ThrowsAsync<ModelContextProtocol.McpProtocolException>(
            () => tool.CallToolAsync(CallToolContextTestHelper.Create(partialArgs), CancellationToken.None).AsTask()).DefaultTimeout();
        Assert.Contains("Missing required arguments", exception2.Message);
    }

    [Fact]
    public async Task ExecuteResourceCommandTool_ReturnsResultJson_WhenCommandReturnsJson()
    {
        var monitor = new TestAuxiliaryBackchannelMonitor();
        var connection = new TestAppHostAuxiliaryBackchannel
        {
            ExecuteResourceCommandResult = new ExecuteResourceCommandResponse
            {
                Success = true,
                Result = """{"token":"token-value","expires":"2025-01-01"}""",
                ResultFormat = "json"
            }
        };
        monitor.AddConnection("hash1", "socket.hash1", connection);

        var tool = new ExecuteResourceCommandTool(monitor, NullLogger<ExecuteResourceCommandTool>.Instance);
        var result = await tool.CallToolAsync(CallToolContextTestHelper.Create(CreateArguments("api-service", "generate-token")), CancellationToken.None).DefaultTimeout();

        Assert.True(result.IsError is null or false);
        Assert.NotNull(result.Content);
        Assert.Equal(2, result.Content.Count);

        var successMessage = result.Content[0] as ModelContextProtocol.Protocol.TextContentBlock;
        Assert.NotNull(successMessage);
        Assert.Contains("successfully", successMessage.Text);

        var resultContent = result.Content[1] as ModelContextProtocol.Protocol.TextContentBlock;
        Assert.NotNull(resultContent);
        Assert.Contains("token-value", resultContent.Text);
        Assert.Contains("expires", resultContent.Text);
    }

    [Fact]
    public async Task ExecuteResourceCommandTool_ReturnsResultText_WhenCommandReturnsTextOnly()
    {
        var monitor = new TestAuxiliaryBackchannelMonitor();
        var connection = new TestAppHostAuxiliaryBackchannel
        {
            ExecuteResourceCommandResult = new ExecuteResourceCommandResponse
            {
                Success = true,
                Result = "eyJhbGciOiJIUzI1NiJ9.token",
                ResultFormat = "text"
            }
        };
        monitor.AddConnection("hash1", "socket.hash1", connection);

        var tool = new ExecuteResourceCommandTool(monitor, NullLogger<ExecuteResourceCommandTool>.Instance);
        var result = await tool.CallToolAsync(CallToolContextTestHelper.Create(CreateArguments("api-service", "generate-token")), CancellationToken.None).DefaultTimeout();

        Assert.True(result.IsError is null or false);
        Assert.NotNull(result.Content);
        Assert.Equal(2, result.Content.Count);

        var resultContent = result.Content[1] as ModelContextProtocol.Protocol.TextContentBlock;
        Assert.NotNull(resultContent);
        Assert.Equal("eyJhbGciOiJIUzI1NiJ9.token", resultContent.Text);
    }
}

