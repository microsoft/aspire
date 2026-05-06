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
    private static IReadOnlyDictionary<string, JsonElement> CreateArguments(string resourceName, string commandName, string? commandArgumentsJson = null)
    {
        var json = commandArgumentsJson is null
            ? $$"""
            {
                "resourceName": "{{resourceName}}",
                "commandName": "{{commandName}}"
            }
            """
            : $$"""
            {
                "resourceName": "{{resourceName}}",
                "commandName": "{{commandName}}",
                "arguments": {{commandArgumentsJson}}
            }
            """;

        // MCP tool arguments JSON can include nested resource command arguments, for example:
        // { "resourceName": "api-service", "commandName": "click", "arguments": { "selector": "#submit" } }
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.EnumerateObject()
            .ToDictionary(p => p.Name, p => p.Value.Clone());
    }

    [Fact]
    public async Task ExecuteResourceCommandTool_ThrowsException_WhenNoAppHostRunning()
    {
        var monitor = new TestAuxiliaryBackchannelMonitor();
        var tool = new ExecuteResourceCommandTool(monitor, NullLogger<ExecuteResourceCommandTool>.Instance);

        var exception = await Assert.ThrowsAsync<ModelContextProtocol.McpProtocolException>(
            () => tool.CallToolAsync(CallToolContextTestHelper.Create(CreateArguments("test-resource", "start")), CancellationToken.None).AsTask()).DefaultTimeout();

        Assert.Contains("No Aspire AppHost", exception.Message);
        Assert.Contains("aspire start", exception.Message);
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
        var result = await tool.CallToolAsync(CallToolContextTestHelper.Create(CreateArguments("api-service", "start")), CancellationToken.None).DefaultTimeout();

        Assert.True(result.IsError is null or false);
        Assert.NotNull(result.Content);
        Assert.Single(result.Content);
        var textContent = result.Content[0] as ModelContextProtocol.Protocol.TextContentBlock;
        Assert.NotNull(textContent);
        Assert.Contains("successfully", textContent.Text);
        Assert.Contains("api-service", textContent.Text);
        Assert.Contains("start", textContent.Text);
    }

    [Fact]
    public async Task ExecuteResourceCommandTool_WithArguments_PassesArgumentsToBackchannel()
    {
        var monitor = new TestAuxiliaryBackchannelMonitor();
        var connection = new TestAppHostAuxiliaryBackchannel
        {
            ExecuteResourceCommandResult = new ExecuteResourceCommandResponse { Success = true }
        };
        monitor.AddConnection("hash1", "socket.hash1", connection);

        var tool = new ExecuteResourceCommandTool(monitor, NullLogger<ExecuteResourceCommandTool>.Instance);
        var result = await tool.CallToolAsync(
            CallToolContextTestHelper.Create(CreateArguments("api-service", "click", """{ "selector": "#submit" }""")),
            CancellationToken.None).DefaultTimeout();

        Assert.True(result.IsError is null or false);
        Assert.NotNull(connection.ExecuteResourceCommandArguments);
        Assert.Equal("#submit", connection.ExecuteResourceCommandArguments.Value.GetProperty("selector").GetString());
    }

    [Fact]
    public async Task ExecuteResourceCommandTool_ThrowsException_WhenArgumentsIsNotObject()
    {
        var monitor = new TestAuxiliaryBackchannelMonitor();
        var connection = new TestAppHostAuxiliaryBackchannel
        {
            ExecuteResourceCommandResult = new ExecuteResourceCommandResponse { Success = true }
        };
        monitor.AddConnection("hash1", "socket.hash1", connection);

        var tool = new ExecuteResourceCommandTool(monitor, NullLogger<ExecuteResourceCommandTool>.Instance);
        var exception = await Assert.ThrowsAsync<ModelContextProtocol.McpProtocolException>(
            () => tool.CallToolAsync(CallToolContextTestHelper.Create(CreateArguments("api-service", "click", """[]""")), CancellationToken.None).AsTask()).DefaultTimeout();

        Assert.Contains("must be a JSON object", exception.Message);
    }

    [Fact]
    public async Task ExecuteResourceCommandTool_ReturnsError_WhenCommandFails()
    {
        var monitor = new TestAuxiliaryBackchannelMonitor();
        var connection = new TestAppHostAuxiliaryBackchannel
        {
            ExecuteResourceCommandResult = new ExecuteResourceCommandResponse
            {
                Success = false,
                Message = "Resource not found"
            }
        };
        monitor.AddConnection("hash1", "socket.hash1", connection);

        var tool = new ExecuteResourceCommandTool(monitor, NullLogger<ExecuteResourceCommandTool>.Instance);

        var result = await tool.CallToolAsync(CallToolContextTestHelper.Create(CreateArguments("nonexistent", "start")), CancellationToken.None).DefaultTimeout();

        Assert.True(result.IsError);
        Assert.Contains(result.Content, c => c is ModelContextProtocol.Protocol.TextContentBlock t && t.Text.Contains("Resource not found"));
    }

    [Fact]
    public async Task ExecuteResourceCommandTool_ReturnsValidationErrors_WhenCommandArgumentsAreInvalid()
    {
        var monitor = new TestAuxiliaryBackchannelMonitor();
        var connection = new TestAppHostAuxiliaryBackchannel
        {
            ExecuteResourceCommandResult = new ExecuteResourceCommandResponse
            {
                Success = false,
                Message = "Command argument validation failed.",
                ValidationErrors =
                [
                    new ResourceCommandArgumentValidationError
                    {
                        ArgumentName = "target",
                        ErrorMessage = "Target must not be prod."
                    }
                ]
            }
        };
        monitor.AddConnection("hash1", "socket.hash1", connection);

        var tool = new ExecuteResourceCommandTool(monitor, NullLogger<ExecuteResourceCommandTool>.Instance);

        var result = await tool.CallToolAsync(CallToolContextTestHelper.Create(CreateArguments("api-service", "validate")), CancellationToken.None).DefaultTimeout();

        Assert.True(result.IsError);
        Assert.Contains(result.Content, c => c is ModelContextProtocol.Protocol.TextContentBlock t &&
            t.Text.Contains("Command argument validation failed.") &&
            t.Text.Contains("target: Target must not be prod."));
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
            () => tool.CallToolAsync(CallToolContextTestHelper.Create(CreateArguments("api-service", "stop")), CancellationToken.None).AsTask()).DefaultTimeout();

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

        // Test with start
        var startResult = await tool.CallToolAsync(CallToolContextTestHelper.Create(CreateArguments("api-service", "start")), CancellationToken.None).DefaultTimeout();
        Assert.True(startResult.IsError is null or false);

        // Test with stop
        var stopResult = await tool.CallToolAsync(CallToolContextTestHelper.Create(CreateArguments("api-service", "stop")), CancellationToken.None).DefaultTimeout();
        Assert.True(stopResult.IsError is null or false);

        // Test with restart
        var restartResult = await tool.CallToolAsync(CallToolContextTestHelper.Create(CreateArguments("api-service", "restart")), CancellationToken.None).DefaultTimeout();
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
    public async Task ExecuteResourceCommandTool_ReturnsResult_WhenCommandReturnsResultData()
    {
        var monitor = new TestAuxiliaryBackchannelMonitor();
        var connection = new TestAppHostAuxiliaryBackchannel
        {
            ExecuteResourceCommandResult = new ExecuteResourceCommandResponse
            {
                Success = true,
                Value = new ExecuteResourceCommandResult
                {
                    Value = "{\"token\": \"abc123\"}",
                    Format = CommandResultFormat.Json
                }
            }
        };
        monitor.AddConnection("hash1", "socket.hash1", connection);

        var tool = new ExecuteResourceCommandTool(monitor, NullLogger<ExecuteResourceCommandTool>.Instance);
        var result = await tool.CallToolAsync(CallToolContextTestHelper.Create(CreateArguments("api-service", "generate-token")), CancellationToken.None).DefaultTimeout();

        Assert.True(result.IsError is null or false);
        Assert.NotNull(result.Content);
        Assert.Equal(2, result.Content.Count);

        var successText = result.Content[0] as ModelContextProtocol.Protocol.TextContentBlock;
        Assert.NotNull(successText);
        Assert.Contains("successfully", successText.Text);

        var resultText = result.Content[1] as ModelContextProtocol.Protocol.TextContentBlock;
        Assert.NotNull(resultText);
        Assert.Contains("abc123", resultText.Text);
    }

    [Fact]
    public async Task ExecuteResourceCommandTool_NoExtraContent_WhenCommandSucceedsWithoutResult()
    {
        var monitor = new TestAuxiliaryBackchannelMonitor();
        var connection = new TestAppHostAuxiliaryBackchannel
        {
            ExecuteResourceCommandResult = new ExecuteResourceCommandResponse { Success = true }
        };
        monitor.AddConnection("hash1", "socket.hash1", connection);

        var tool = new ExecuteResourceCommandTool(monitor, NullLogger<ExecuteResourceCommandTool>.Instance);
        var result = await tool.CallToolAsync(CallToolContextTestHelper.Create(CreateArguments("api-service", "start")), CancellationToken.None).DefaultTimeout();

        Assert.True(result.IsError is null or false);
        Assert.NotNull(result.Content);
        Assert.Single(result.Content);
    }
}
