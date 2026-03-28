// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

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
                Result = "{\"items\": [\"a\", \"b\"]}",
                ResultFormat = "json"
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
            CancellationToken.None).DefaultTimeout();

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
            CancellationToken.None).DefaultTimeout();

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
                ErrorMessage = "Validation failed",
                Result = "{\"errors\": [\"invalid host\"]}",
                ResultFormat = "json"
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
            CancellationToken.None).DefaultTimeout();

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
                Result = "some output",
                ResultFormat = "text"
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
            CancellationToken.None).DefaultTimeout();

        Assert.Equal(0, exitCode);
        // Status messages should be routed to stderr
        Assert.Equal(ConsoleOutput.Error, interactionService.Console);
    }
}
