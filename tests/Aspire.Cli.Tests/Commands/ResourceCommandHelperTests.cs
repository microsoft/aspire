// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using Aspire.Cli.Backchannel;
using Aspire.Cli.Commands;
using Aspire.Cli.Interaction;
using Aspire.Cli.Tests.TestServices;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aspire.Cli.Tests.Commands;

public class ResourceCommandHelperTests
{
    [Fact]
    public async Task ExecuteGenericCommandAsync_WithResult_OutputsRawText()
    {
        var connection = new TestAppHostAuxiliaryBackchannel
        {
            ExecuteResourceCommandResult = new ExecuteResourceCommandResponse
            {
                Success = true,
                Value = new ExecuteResourceCommandResult
                {
                    Value = "{\"items\": [\"a\", \"b\"]}",
                    Format = CommandResultFormat.Json
                }
            }
        };

        string? capturedRawText = null;
        var interactionService = new TestInteractionService
        {
            DisplayRawTextCallback = (text) => capturedRawText = text
        };

        var exitCode = await ResourceCommandHelper.ExecuteGenericCommandAsync(
            connection,
            interactionService,
            NullLogger.Instance,
            "myResource",
            "generate-token",
            arguments: null,
            cancellationToken: CancellationToken.None).DefaultTimeout();

        Assert.Equal(0, exitCode);
        Assert.NotNull(capturedRawText);
        // Verify the raw result is passed through without any escaping
        Assert.Equal("{\"items\": [\"a\", \"b\"]}", capturedRawText);
    }

    [Fact]
    public async Task ExecuteGenericCommandAsync_WithoutResult_DoesNotCallDisplayMessage()
    {
        var connection = new TestAppHostAuxiliaryBackchannel
        {
            ExecuteResourceCommandResult = new ExecuteResourceCommandResponse { Success = true }
        };

        var displayRawTextCalled = false;
        var interactionService = new TestInteractionService
        {
            DisplayRawTextCallback = (_) => displayRawTextCalled = true
        };

        var exitCode = await ResourceCommandHelper.ExecuteGenericCommandAsync(
            connection,
            interactionService,
            NullLogger.Instance,
            "myResource",
            "start",
            arguments: null,
            cancellationToken: CancellationToken.None).DefaultTimeout();

        Assert.Equal(0, exitCode);
        Assert.False(displayRawTextCalled);
    }

    [Fact]
    public async Task ExecuteGenericCommandAsync_ErrorWithResult_OutputsRawText()
    {
        var connection = new TestAppHostAuxiliaryBackchannel
        {
            ExecuteResourceCommandResult = new ExecuteResourceCommandResponse
            {
                Success = false,
                Message = "Validation failed",
                Value = new ExecuteResourceCommandResult
                {
                    Value = "{\"errors\": [\"invalid host\"]}",
                    Format = CommandResultFormat.Json
                }
            }
        };

        string? capturedRawText = null;
        var interactionService = new TestInteractionService
        {
            DisplayRawTextCallback = (text) => capturedRawText = text
        };

        var exitCode = await ResourceCommandHelper.ExecuteGenericCommandAsync(
            connection,
            interactionService,
            NullLogger.Instance,
            "myResource",
            "validate-config",
            arguments: null,
            cancellationToken: CancellationToken.None).DefaultTimeout();

        Assert.NotEqual(0, exitCode);
        Assert.NotNull(capturedRawText);
        Assert.Equal("{\"errors\": [\"invalid host\"]}", capturedRawText);
    }

    [Fact]
    public async Task ExecuteGenericCommandAsync_RoutesStatusToStderr_ResultToStdout()
    {
        var connection = new TestAppHostAuxiliaryBackchannel
        {
            ExecuteResourceCommandResult = new ExecuteResourceCommandResponse
            {
                Success = true,
                Value = new ExecuteResourceCommandResult
                {
                    Value = "some output",
                    Format = CommandResultFormat.Text
                }
            }
        };

        var interactionService = new TestInteractionService
        {
            DisplayRawTextCallback = (_) => { }
        };

        var exitCode = await ResourceCommandHelper.ExecuteGenericCommandAsync(
            connection,
            interactionService,
            NullLogger.Instance,
            "myResource",
            "my-command",
            arguments: null,
            cancellationToken: CancellationToken.None).DefaultTimeout();

        Assert.Equal(0, exitCode);
        // Status messages should be routed to stderr
        Assert.Equal(ConsoleOutput.Error, interactionService.Console);
    }

    [Fact]
    public async Task ExecuteGenericCommandAsync_WithArguments_PassesArgumentsToBackchannel()
    {
        var connection = new TestAppHostAuxiliaryBackchannel
        {
            ExecuteResourceCommandResult = new ExecuteResourceCommandResponse { Success = true }
        };

        var interactionService = new TestInteractionService();

        // Command arguments JSON is expected to be an object, for example: { "selector": "#submit" }.
        using var document = JsonDocument.Parse("""{ "selector": "#submit" }""");
        var arguments = document.RootElement.Clone();

        var exitCode = await ResourceCommandHelper.ExecuteGenericCommandAsync(
            connection,
            interactionService,
            NullLogger.Instance,
            "myResource",
            "click",
            arguments,
            CancellationToken.None).DefaultTimeout();

        Assert.Equal(0, exitCode);
        Assert.NotNull(connection.ExecuteResourceCommandArguments);
        Assert.Equal("#submit", connection.ExecuteResourceCommandArguments.Value.GetProperty("selector").GetString());
    }
}
